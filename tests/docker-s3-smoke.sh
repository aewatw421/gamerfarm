#!/bin/sh
# The real image against a real bucket, in CI: does a setting written before the container is thrown away come back
# after it?
#
#   sh tests/docker-s3-smoke.sh <image> <endpoint> <bucket> <access-key-id> <secret-access-key> [prefix]
#
# What it proves, in order:
#   1. the image starts with NO volume at all (docker run, and the mount list is checked to be empty),
#   2. the bucket is written to while it runs ('cloudsync now' says how many files went up),
#   3. `docker stop` is a clean shutdown that saves the last changes,
#   4. a brand-new container, with an empty /data, pulls the state back: the setting changed in (2) is still there,
#      and the restore is reported in its log.
#
# The bucket is created by the CI job; this script never creates one and never removes anything but its own containers.
set -eu

IMAGE=${1:?image}
ENDPOINT=${2:?endpoint}
BUCKET=${3:?bucket}
KEY=${4:?access key id}
SECRET=${5:?secret access key}
PREFIX=${6:-gamerfarm}

# The endpoint as the HOST sees it (127.0.0.1) is not the endpoint the container can reach: inside a container, 127.0.0.1
# is the container itself. host.docker.internal is the way back to the host, and --add-host puts it in the container's
# /etc/hosts on Linux as well as Docker Desktop. Anything else is used as it is (a real bucket, a compose service name).
case "$ENDPOINT" in
	*127.0.0.1*|*localhost*) INSIDE=$(echo "$ENDPOINT" | sed -e 's/127\.0\.0\.1/host.docker.internal/' -e 's/localhost/host.docker.internal/') ;;
	*) INSIDE=$ENDPOINT ;;
esac

PASSWORD=a-long-smoke-test-password-123
FIRST=gamerfarm-s3-smoke-1
SECOND=gamerfarm-s3-smoke-2
PORT=7242

cleanup() {
	docker rm -f "$FIRST" "$SECOND" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

fail() {
	echo "FAIL: $*"
	for c in "$FIRST" "$SECOND"; do
		echo "== docker logs $c"
		docker logs "$c" 2>&1 | tail -n 40 || true
	done
	exit 1
}

start() {
	name=$1

	docker rm -f "$name" >/dev/null 2>&1 || true
	docker run -d --name "$name" --init \
		--add-host host.docker.internal:host-gateway \
		-e TZ=UTC \
		-e GAMERFARM_WEB_HOST=0.0.0.0 \
		-e GAMERFARM_WEB_PASSWORD="$PASSWORD" \
		-e GAMERFARM_S3_BUCKET="$BUCKET" \
		-e GAMERFARM_S3_ENDPOINT="$INSIDE" \
		-e GAMERFARM_S3_ACCESS_KEY_ID="$KEY" \
		-e GAMERFARM_S3_SECRET_ACCESS_KEY="$SECRET" \
		-e GAMERFARM_S3_PREFIX="$PREFIX" \
		-p "127.0.0.1:$PORT:7242" \
		"$IMAGE" >/dev/null

	# the image must not ask for a volume of its own: that is the whole point of the bucket
	mounts=$(docker inspect -f '{{len .Mounts}}' "$name")
	[ "$mounts" = "0" ] || fail "$name came with $mounts mount(s) - the image is supposed to run with no disk"

	for _ in $(seq 1 90); do
		if curl -fs "http://127.0.0.1:$PORT/api/ping" >/dev/null 2>&1; then
			return 0
		fi

		# It is allowed to give up: with GAMERFARM_S3_REQUIRED (the default) an unreadable bucket ends the start rather
		# than running on defaults. Say so at once instead of waiting out the full 90 seconds.
		if [ "$(docker inspect -f '{{.State.Running}}' "$name")" != "true" ]; then
			fail "$name exited instead of starting"
		fi

		sleep 1
	done

	fail "$name never answered on /api/ping"
}

login() {
	curl -fs -X POST "http://127.0.0.1:$PORT/api/login" -H 'Content-Type: application/json' \
		-d "{\"Password\":\"$PASSWORD\"}" | sed -E 's/.*"token":"([^"]+)".*/\1/'
}

command() {
	token=$1
	line=$2

	curl -fs -X POST "http://127.0.0.1:$PORT/api/command" \
		-H 'Content-Type: application/json' -H "Authorization: Bearer $token" \
		-d "{\"line\":\"$line\"}" | sed -E 's/.*"output":"(.*)","holdsSecret".*/\1/'
}

echo "== 1. first container, no volume, state in s3://$BUCKET/$PREFIX"
start "$FIRST"
TOKEN=$(login)
echo "-- cloudsync:"
command "$TOKEN" cloudsync

echo "-- changing a setting that will be looked for in the next container"
command "$TOKEN" 'set UpdateCheckHours 5'
[ "$(command "$TOKEN" 'cloudsync now' | grep -c 'saved to s3://')" -ge 1 ] || fail "cloudsync now saved nothing"

echo "== 2. docker stop is a save, not a rollback"
docker stop -t 60 "$FIRST" >/dev/null
docker logs "$FIRST" 2>&1 | grep -q 'shutting down' || fail "no clean shutdown in the log"
docker logs "$FIRST" 2>&1 | grep -q 'cloud: .*saved to s3://' || fail "the shutdown saved nothing to the bucket"
docker rm -f "$FIRST" >/dev/null

echo "== 3. a brand-new container, with nothing on disk, gets it all back"
start "$SECOND"
docker logs "$SECOND" 2>&1 | grep -Eq 'cloud: [0-9]+ file\(s\) restored' || fail "nothing was restored from the bucket"

TOKEN=$(login)
echo "-- config after the restore:"
command "$TOKEN" config | grep -E 'UpdateCheckHours' || fail "the restored settings are missing UpdateCheckHours"
[ "$(command "$TOKEN" config | grep -c 'UpdateCheckHours = 5')" -eq 1 ] || fail "the setting changed in the first container did not survive"

echo "-- cloudsync:"
command "$TOKEN" cloudsync

echo "PASS: the state lived in the bucket, not in the container"