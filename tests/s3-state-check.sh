#!/bin/sh
# The state in a bucket, without Docker: the real binary against tests/s3-mock.mjs.
#
#   sh tests/s3-state-check.sh [path-to-gamerFarm.dll]
#
# What it does, in order - the same promise docs/docker-s3.md makes, minus the container:
#
#   1. a start with an EMPTY data folder and an empty bucket: the settings, the key and the state files go up,
#   2. a setting is changed through the dashboard API and the change reaches the bucket,
#   3. the process is stopped with SIGTERM (what `docker stop` sends): a clean shutdown, and its last save goes up,
#   4. the data folder is DELETED - as a container's /data is when the container is thrown away,
#   5. a fresh start with the same environment pulls it all back: the setting changed in (2) is still there, the AES
#      key is byte for byte the one that was uploaded (so saved logins still decrypt), and the instance lock is NOT in
#      the bucket.
#
# Exits non-zero with the failing step named. Everything lands in a temp folder; nothing of the caller's is touched.
set -eu

DLL=${1:-src/GamerFarm/bin/Release/net10.0/gamerFarm.dll}
PORT_APP=${PORT_APP:-7391}
PORT_S3=${PORT_S3:-9391}
PASSWORD=state-check-password-123

[ -f "$DLL" ] || { echo "FAIL: no $DLL - build it first: dotnet build src/GamerFarm/GamerFarm.csproj -c Release"; exit 1; }

WORK=$(mktemp -d)
DATA=$WORK/data
BUCKET=$WORK/bucket
LOG=$WORK/app.log
PREFIX=gamerfarm/state-check
BUCKET_NAME=check

cleanup() {
	[ -n "${APP_PID:-}" ] && kill -TERM "$APP_PID" 2>/dev/null || true
	[ -n "${MOCK_PID:-}" ] && kill "$MOCK_PID" 2>/dev/null || true
	rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

fail() {
	echo "FAIL: $*"
	echo "== the app said:"
	tail -n 40 "$LOG" 2>/dev/null || true
	exit 1
}

export GAMERFARM_S3_BUCKET=$BUCKET_NAME
export GAMERFARM_S3_ENDPOINT=http://127.0.0.1:$PORT_S3
export GAMERFARM_S3_ACCESS_KEY_ID=test
export GAMERFARM_S3_SECRET_ACCESS_KEY=test-secret
export GAMERFARM_S3_PREFIX=$PREFIX
export GAMERFARM_WEB_PASSWORD=$PASSWORD
export GAMERFARM_WEB_HOST=127.0.0.1
export GAMERFARM_WEB_PORT=$PORT_APP
export GAMERFARM_PING_URL=http://127.0.0.1:9/api/farm/ping
export TZ=UTC

node "$(dirname "$0")/s3-mock.mjs" --dir "$BUCKET" --port "$PORT_S3" > "$WORK/mock.log" 2>&1 &
MOCK_PID=$!

for _ in $(seq 1 30); do
	curl -s -o /dev/null "http://127.0.0.1:$PORT_S3/" && break
	sleep 0.5
done

start_app() {
	: > "$LOG"
	dotnet "$DLL" --path "$DATA" --no-gui >> "$LOG" 2>&1 &
	APP_PID=$!

	for _ in $(seq 1 90); do
		if curl -fs "http://127.0.0.1:$PORT_APP/api/ping" > /dev/null 2>&1; then
			return 0
		fi
		kill -0 "$APP_PID" 2>/dev/null || fail "the app exited while starting"
		sleep 1
	done

	fail "the dashboard never answered on $PORT_APP"
}

login() {
	curl -fs -X POST "http://127.0.0.1:$PORT_APP/api/login" -H 'Content-Type: application/json' \
		-d "{\"Password\":\"$PASSWORD\"}" | sed -E 's/.*"token":"([^"]+)".*/\1/'
}

command() {
	curl -fs -X POST "http://127.0.0.1:$PORT_APP/api/command" \
		-H 'Content-Type: application/json' -H "Authorization: Bearer $1" \
		-d "{\"line\":\"$2\"}"
}

echo "== 1. a fresh start, an empty bucket, no volume"
start_app
grep -q 'cloud: state in s3://' "$LOG" || fail "the app didn't say where the state is"

TOKEN=$(login)
command "$TOKEN" 'cloudsync now' > /dev/null
[ -f "$BUCKET/$BUCKET_NAME/$PREFIX/config/gamerFarm.json" ] || fail "the settings never reached the bucket"

echo "== 2. a setting changed in the dashboard reaches the bucket"
command "$TOKEN" 'set UpdateCheckHours 5' > /dev/null
command "$TOKEN" 'cloudsync now' > /dev/null
grep -q '"UpdateCheckHours": 5' "$BUCKET/$BUCKET_NAME/$PREFIX/config/gamerFarm.json" \
	|| fail "the changed setting isn't in the bucket"

KEY_BEFORE=$(md5sum "$BUCKET/$BUCKET_NAME/$PREFIX/config/state/secret.key" | cut -d' ' -f1)

echo "== 3. SIGTERM (what docker stop sends) is a clean shutdown that saves"
kill -TERM "$APP_PID"
for _ in $(seq 1 40); do
	kill -0 "$APP_PID" 2>/dev/null || break
	sleep 1
done
kill -0 "$APP_PID" 2>/dev/null && fail "still running 40s after SIGTERM"
APP_PID=
grep -q 'shutting down' "$LOG" || fail "no clean shutdown in the log"
grep -q 'cloud: .*saved to s3://' "$LOG" || fail "the shutdown saved nothing to the bucket"

if find "$BUCKET" -name 'instance.lock' | grep -q .; then
	fail "the single-instance lock was uploaded - the next start would find a stale one"
fi

echo "== 4. the data folder is thrown away, like a container's"
rm -rf "$DATA"

echo "== 5. a fresh start gets it all back"
start_app
grep -Eq 'cloud: [0-9]+ file\(s\) restored' "$LOG" || fail "nothing was restored"

TOKEN=$(login)
[ "$(grep -c '"UpdateCheckHours": 5' "$DATA/config/gamerFarm.json")" -eq 1 ] \
	|| fail "the setting changed in step 2 did not survive the re-create ($(grep -o 'UpdateCheckHours[^,]*' "$DATA/config/gamerFarm.json" || echo 'not there at all'))"

KEY_AFTER=$(md5sum "$DATA/config/state/secret.key" | cut -d' ' -f1)
[ "$KEY_BEFORE" = "$KEY_AFTER" ] || fail "the AES key came back different - saved Steam logins would no longer decrypt"

echo "-- cloudsync:"
command "$TOKEN" cloudsync | sed -E 's/.*"output":"(.*)","holdsSecret".*/\1/' | sed 's/\\n/\n/g'

echo "PASS: the state lived in the bucket, not in the data folder"