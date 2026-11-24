[Wiki home](README.md) · [Every command](COMMANDS.md) · [Linux, Mac and Docker](linux-docker-vps.md) · [Front page](../README.md)

# No disk: the state in an S3 bucket

A container can be thrown away at any moment: a redeploy, a node drained, a crash loop, a platform that only keeps
the image. gamer.farm keeps everything that matters in one folder - `config/` holds the settings, every account's
config, their saved Steam logins, the AES key that opens those logins, and the state files the modules write;
`plugins/` holds the add-ons. On a PC that folder is the installation's memory. In a container with no volume it is
gone with the container, and every start asks for the passwords again.

Set `GAMERFARM_S3_BUCKET` and that folder lives in a bucket instead:

* **start** - the bucket is listed and everything in it is fetched into `/data` **before the app reads any of it**,
  so the accounts, their tokens and the key are in place before the first sign-in. The dashboard password, the
  Telegram/Discord tokens and every other secret still decrypt, because `config/state/secret.key` comes with them.
* **running** - every save is noticed (a file watcher, plus a sweep every 30 seconds) and put back within a couple of
  seconds. A removed account's files are removed from the bucket too.
* **stopping** - `docker stop` is a clean shutdown: the accounts sign out, the lifetime totals are written, and one
  last sweep puts whatever changed in the final second into the bucket.

No volume, no bind mount, no sidecar: one container and five environment variables.

## The variables

Everything is `GAMERFARM_S3_*`, and every one of them is optional except the bucket and the two credentials.

| Variable | Default | What it does |
| --- | --- | --- |
| `GAMERFARM_S3_BUCKET` | *(unset: off)* | The bucket. Set it and the state moves there; without it nothing changes at all. |
| `GAMERFARM_S3_ACCESS_KEY_ID` | - | The key id. Also read from `AWS_ACCESS_KEY_ID`. |
| `GAMERFARM_S3_SECRET_ACCESS_KEY` | - | The secret. Also read from `AWS_SECRET_ACCESS_KEY`. |
| `GAMERFARM_S3_SESSION_TOKEN` | - | For temporary credentials (STS, an assumed role, R2's tokens). Also `AWS_SESSION_TOKEN`. |
| `GAMERFARM_S3_ENDPOINT` | AWS's own | Anything that isn't AWS: `https://<account>.r2.cloudflarestorage.com`, `https://s3.eu-central-003.backblazeb2.com`, `http://minio:9000`. |
| `GAMERFARM_S3_REGION` | `us-east-1` | The region, as the store wants it. R2 wants `auto`. |
| `GAMERFARM_S3_PREFIX` | `gamerfarm` | Every object sits under it, so one bucket can hold several farms. |
| `GAMERFARM_S3_PATH_STYLE` | on when an endpoint is set | `/bucket/key` instead of the bucket in the host name. MinIO, R2 and B2 need it - it is already on for them. |
| `GAMERFARM_S3_FOLDERS` | `config,plugins` | What is kept. Add `backups` to keep the backup zips there too, `logs` for the log files. `config` is always in the list. |
| `GAMERFARM_S3_REQUIRED` | `1` | The start fails if the bucket can't be read, instead of running on defaults that would then be written over the real settings. `0` starts anyway, and that run keeps its state in the container. |
| `GAMERFARM_S3_PULL` | `1` | `0` keeps the bucket's copy and holds the local one (a warm volume, a restored backup); changes still go up. |
| `GAMERFARM_S3_INTERVAL_SECONDS` | `30` | How often the folders are swept, besides the watcher's immediate reaction. |
| `GAMERFARM_S3_VERBOSE` | `0` | A debug line for every file pulled, saved or removed. |

Each credential can also be given as a file, which is what a Kubernetes secret or a Docker secret mounts:
`GAMERFARM_S3_ACCESS_KEY_FILE`, `GAMERFARM_S3_SECRET_KEY_FILE`, `GAMERFARM_S3_SESSION_TOKEN_FILE`.

`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_REGION` and `AWS_DEFAULT_REGION` are read as
the fallbacks for the four, so a container that already has AWS credentials needs only the bucket's name.

## By provider

**AWS S3.** Nothing but the bucket and the keys. For a bucket in another region add `GAMERFARM_S3_REGION`
(`eu-central-1`, say) - the endpoint follows it. A key needs `s3:GetObject`, `s3:PutObject`, `s3:DeleteObject` and
`s3:ListBucket` on `arn:aws:s3:::<bucket>` and `arn:aws:s3:::<bucket>/*`.

**Cloudflare R2.**

```yaml
GAMERFARM_S3_BUCKET: gamerfarm
GAMERFARM_S3_ENDPOINT: https://<account-id>.r2.cloudflarestorage.com
GAMERFARM_S3_REGION: auto
GAMERFARM_S3_ACCESS_KEY_ID: <R2 access key id>
GAMERFARM_S3_SECRET_ACCESS_KEY: <R2 secret access key>
```

The keys come from *R2 › Manage R2 API Tokens*, with **Object Read & Write** on that bucket. They are not the
Cloudflare global API key.

**Backblaze B2.** Create an application key for the bucket, then
`GAMERFARM_S3_ENDPOINT=https://s3.<cluster>.backblazeb2.com` and `GAMERFARM_S3_REGION=<cluster>`
(`us-west-004`, `eu-central-003`, ...).

**MinIO, Ceph, Garage, anything self-hosted.** `GAMERFARM_S3_ENDPOINT=http://minio:9000` and path style is already
on. A plain HTTP endpoint is fine inside a compose network; outside it, put TLS in front.

## What it does not do

* **One writer per prefix.** Two containers sharing a `GAMERFARM_S3_PREFIX` would overwrite each other's settings.
  Use one prefix per farm, and let the platform keep one replica running (a Deployment with `replicas: 1`, not an
  autoscaler).
* **Deleted files.** A file removed here is removed from the bucket - that is how a removed account stops coming
  back. If a whole folder vanishes at once (40+ files, more than half of them) it is *not* deleted and the log says
  so: an unmounted volume looks exactly like that. `cloudsync now` in the console removes them once you're sure.
* **A failed pull blocks writing.** If the bucket can't be read at start, that run writes nothing to it. Going on and
  uploading the defaults would put an empty config over a working one.
* **The single-instance lock is never synced.** `config/state/instance.lock` belongs to the copy running now; a
  pulled one would be a stale lock on the next start.
* **Log files are not kept by default.** They are appended to constantly, so they would be a steady trickle of small
  uploads. Add `logs` to `GAMERFARM_S3_FOLDERS` if you want them, or keep them on the host.

## Checking it

The console, the dashboard's command box, Telegram and Discord all answer `cloudsync`:

```
cloud: s3://gamerfarm/gamerfarm - config, plugins
  47 file(s) here, 3 saved, 44 restored, 0 removed this run
  watching for changes, and a sweep every 30s; last round 14:02:11
```

`cloudsync now` saves everything this second instead of waiting for the next sweep.

The log says the same things at the moments they happen:

```
cloud: state in s3://gamerfarm/gamerfarm (config, plugins)
cloud: 44 file(s) restored from s3://gamerfarm/gamerfarm
cloud: s3://gamerfarm/gamerfarm is the state; every change goes back there (44 file(s) here)
...
cloud: 2 file(s) saved to s3://gamerfarm/gamerfarm
```

## Moving an existing install into a bucket

Copy the folders up once, with the same layout the app expects - `<prefix>/config/...` and `<prefix>/plugins/...`:

```
mc alias set farm https://s3.eu-central-1.amazonaws.com <key> <secret>
mc mirror --overwrite /path/to/gamer.farm/config  farm/gamerfarm/gamerfarm/config
mc mirror --overwrite /path/to/gamer.farm/plugins farm/gamerfarm/gamerfarm/plugins
```

Then start the container with that prefix. It pulls everything, and the accounts are signed in from the first
second - no Steam Guard code, no password. The bucket holds the key those logins are encrypted with, so treat it
like the machine itself: private, and with credentials only the container has.

Same idea for the other way round: point `mc mirror` in the reverse direction at an empty folder and a backup is
restored anywhere.

## When something is wrong

| What the log says | What it is |
| --- | --- |
| `S3 403 SignatureDoesNotMatch` | The secret is wrong, or the endpoint is another provider's. R2 keys are not Cloudflare API tokens. |
| `S3 403 AccessDenied` | The key is valid but not for this bucket. Needs Get/Put/Delete + ListBucket on it. |
| `S3 404 NoSuchBucket` | The bucket's name (and region) is wrong, or it was never created. |
| `S3 301` / `PermanentRedirect` | `GAMERFARM_S3_REGION` doesn't match the bucket's region. |
| `Connection refused` on a `http://` endpoint | The endpoint is wrong, or the store isn't up. |
| `the state was required and could not be read` | `GAMERFARM_S3_REQUIRED` is on (the default) and the bucket didn't answer. The app exits rather than run on defaults. Fix the bucket, or set it to `0`. |
| Unknown `x-amz-...` errors from a self-hosted store | It probably doesn't do SigV4 with a payload hash. Use `GAMERFARM_S3_VERBOSE=1` and its own log. |

A dry run, without touching the app: `docker run --rm -e ... <image> --help` won't do it - but the start itself is
the check, and it says exactly what failed before it reads a single setting.

## How it is built

`src/GamerFarm/Core/S3Store.cs` is a small SigV4 client over `HttpClient` (GET, PUT, DELETE, ListObjectsV2) - no AWS
SDK, so the image gains no dependency and any S3-compatible store works. `src/GamerFarm/Core/CloudSync.cs` is the
pull/push engine: the watcher, the sweep, the compares (size, write time, MD5 of a file last put there), the delete
guard, and the shutdown flush. `tests/s3-mock.mjs` is a SigV4-verifying stand-in for a bucket, used by the tests;
`tests/docker-s3-smoke.sh` runs the real image against a real MinIO in CI and checks that a setting written before a
restart is still there after it.