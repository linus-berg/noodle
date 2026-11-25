<p align="center">
  <img src="assets/logo.svg" alt="Noodle logo: chopsticks lifting noodles out of a bowl, a tiny tarball among them" width="160">
</p>

<h1 align="center">Noodle</h1>

<p align="center"><b>An offline npm registry for air-gapped networks, backed only by S3.</b></p>

Noodle answers like `registry.npmjs.org`: the same URLs, the same documents,
field for field. Point `registry.npmjs.org` (and `registry.yarnpkg.com`,
Yarn's default) at Noodle in your internal DNS, and npm, npx, Yarn classic,
Yarn berry, pnpm and bun work with their **stock configuration**: `install`,
`npm ci` with lockfiles created online, `search`, `view`, `outdated`, and
`audit` once advisory data has been imported.

```
 developer / CI ──https://registry.npmjs.org──▶ (internal DNS) ──▶ Noodle ──▶ S3 (MinIO, ...)
                                                                    ▲
 tarballs (curl, scripts, ...) ──PUT /-/noodle/upload + API key─────┘
```

* **Tarballs in, everything else derived.** Packages arrive as `.tgz` files,
  nothing more: no packuments, no publish times, no dist-tags. Noodle reads
  `package.json` from each tarball, normalizes it the way `npm publish` does,
  computes `dist.integrity`, `shasum`, `fileCount` and `unpackedSize`, and
  generates both packument flavours (abbreviated "corgi" for installs, full for
  `npm view`).
* **Arrival order does not matter.** `latest`, the top-level fields, the
  version list and the search entry depend only on which versions are present:
  uploading 2.0.0 before 1.0.0 gives the same result as the other way round.
* **Exact bytes and names.** Tarballs are stored unmodified, so the integrity
  in lockfiles created online matches. Names are case sensitive (`JSONStream`)
  and scoped names (`@types/node`) work in every spelling npm clients use.
* **Search** (`npm search`) with npm's qualifiers (`keywords:`, `author:`,
  `maintainer:`, `scope:`, `is:`/`not:` `unstable`/`insecure`,
  `boost-exact:`), ranked by text relevance and local download counts.
* **npm audit offline** from imported OSV advisories (the GitHub Advisory
  Database). Until advisory data is imported, audits fail visibly instead of
  reporting a reassuring "0 vulnerabilities".
* **S3 only.** No database, no local state. Several instances can run against
  one bucket behind a load balancer.
* **Built for a large mirror.** Tested with a catalog of one million packages
  and ten million versions (690 MiB of memory, loaded in 3 seconds) and with
  the largest real packages (`next`, 4,049 versions) uploaded in random order.

## Contents

1. [Quick start](#quick-start)
2. [Pointing registry.npmjs.org at Noodle](#pointing-registrynpmjsorg-at-noodle)
3. [Uploading tarballs](#uploading-tarballs)
4. [Security advisories (npm audit)](#security-advisories-npm-audit)
5. [HTTP API](#http-api)
6. [Configuration](#configuration)
7. [How it works](#how-it-works)
8. [Operations](#operations)
9. [Differences from registry.npmjs.org](#differences-from-registrynpmjsorg)
10. [Development](#development)

## Quick start

You need an S3 compatible store (MinIO, RustFS, Ceph, AWS S3, ...) and a TLS
certificate for `registry.npmjs.org` and `registry.yarnpkg.com` from a CA your
clients trust.

```bash
cd deploy
./generate-certs.sh npm.corp.example        # or issue a certificate from your own CA
cat > .env <<EOF
NOODLE_API_KEY=$(openssl rand -hex 24)
S3_URL=https://minio.corp.example:9000
S3_ACCESS_KEY=...
S3_SECRET_KEY=...
EOF
docker compose up -d
curl --cacert certs/ca.crt https://npm.corp.example/health/ready
```

The bucket (`npm` by default) is created on first start. Then upload a
tarball:

```bash
curl -X PUT -H "Authorization: Bearer $NOODLE_API_KEY" \
     --data-binary @express-5.1.0.tgz https://npm.corp.example/-/noodle/upload
```

## Pointing registry.npmjs.org at Noodle

1. **DNS**: resolve `registry.npmjs.org` and `registry.yarnpkg.com` to the
   Noodle host (or load balancer). npm, pnpm and bun use the first, Yarn
   (classic and berry) the second, and lockfiles created online contain
   tarball URLs on both.
2. **TLS**: clients connect to `https://registry.npmjs.org`, so Noodle needs a
   certificate for both names. `deploy/generate-certs.sh` creates a CA and such
   a certificate (plus any extra names you pass); or issue one from your
   organisation's CA. Configure it with
   `ASPNETCORE_Kestrel__Certificates__Default__Path` / `__KeyPath` (PEM), or
   terminate TLS at a reverse proxy (then keep
   `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, already set in the image, and
   make the proxy pass `Host` and `X-Forwarded-Proto`).
3. **Trust the CA on clients.** Node.js does not use the operating system's
   certificate store by default, so installing the CA system wide is not
   enough. Any one of these works (each was tested against Noodle on a network
   without internet access):

   | Client | Setting |
   |---|---|
   | npm, npx, pnpm, Yarn classic, Yarn berry, bun | environment variable `NODE_EXTRA_CA_CERTS=/path/to/ca.crt` |
   | Node.js 22.15+ / 23.8+ based tools | CA in the OS store and `NODE_OPTIONS=--use-system-ca` |
   | npm, pnpm | `cafile=/path/to/ca.crt` in `.npmrc` (`npm config set cafile ...`) |
   | Yarn classic | `yarn config set cafile /path/to/ca.crt` |
   | Yarn berry | `httpsCaFilePath: /path/to/ca.crt` in `.yarnrc.yml` (or `~/.yarnrc.yml`) |
   | bun | `NODE_EXTRA_CA_CERTS`, or `[install]` `cafile = "/path/to/ca.crt"` in `bunfig.toml` |

   `NODE_EXTRA_CA_CERTS` is the simplest for machines and build images:
   `ENV NODE_EXTRA_CA_CERTS=/usr/local/share/ca-certificates/internal-ca.crt`.
   npm's and pnpm's `cafile` replace the built-in CA list (the internal CA is
   then the only one trusted), which is fine without internet access. Without
   any of them, clients fail with `UNABLE_TO_VERIFY_LEAF_SIGNATURE`.

Nothing else changes on clients. Lockfiles created online keep working: their
integrity values match, and npm rewrites the tarball URLs in them to the
configured registry anyway.

Tarball URLs in responses are built from the scheme and host each request
used, so the same server also works under an internal name
(`registry=https://npm.corp.example/` in `.npmrc`): lockfiles then record that
name. Set `Noodle__PublicBaseUrl` to force one base URL.

## Uploading tarballs

An upload is a plain HTTP `PUT` (or `POST`) of the `.tgz` file as the request
body, with the API key in `Authorization: Bearer` (or `X-Api-Key`). The file
name does not matter: name and version are read from the tarball's
`package.json`. The examples use the development server; replace the URL and
key with yours (and add `--cacert ca.crt` if your CA is not trusted system
wide).

```bash
NOODLE=http://localhost:5090
KEY=dev-api-key

# Download a tarball from registry.npmjs.org (on a machine with internet access) ...
curl -fsSLO https://registry.npmjs.org/express/-/express-5.1.0.tgz

# ... and upload it, unchanged.
curl -X PUT -H "Authorization: Bearer $KEY" --data-binary @express-5.1.0.tgz "$NOODLE/-/noodle/upload"
```

```json
{"name":"express","version":"5.1.0","status":"created",
 "integrity":"sha512-DT9ck5YIRU+8GYzzU5kT3eHGA5iL+1Zd0EutOmTE9Dtk+Tvuzd23VBU+ec7HPNSTxXYO55gPV/hq4pSBJDjFpA==",
 "tarball":"http://localhost:5090/express/-/express-5.1.0.tgz"}
```

| Status | Meaning |
|--------|---------|
| 201 | Version added |
| 200 | The identical tarball was already there (repeating an upload is fine) |
| 409 | A *different* tarball with this name and version exists; the first one stays |
| 400 | Not a usable tarball: damaged, truncated, no `package.json`, invalid name or version |
| 401 | Missing or wrong API key (403: no API key configured at all) |
| 413 | Larger than `Noodle__MaxTarballBytes` |
| 503 | Storage unavailable or busy; safe to retry |

Every byte of the gzip stream and the tar archive is read before anything is
stored, and the gzip checksum is verified, so damaged or truncated files are
rejected rather than served.

Uploads are idempotent and can run in parallel, also through several
instances, so a whole folder can be pushed in one go:

```bash
find tarballs/ -name '*.tgz' -print0 |
  xargs -0 -P 8 -I{} curl -s -o /dev/null -w "%{http_code} {}\n" -X PUT \
    -H "Authorization: Bearer $KEY" --data-binary @{} "$NOODLE/-/noodle/upload"
```

While many versions of one package arrive close together (a bulk load),
uploads are acknowledged as soon as the tarball is stored, and the package is
rewritten once for every batch of versions rather than once per version; they
appear within a second or two. Add `?wait=true` to an upload to wait until it
is visible.

`scripts/mirror-packages.sh` downloads from registry.npmjs.org and uploads, by
name, by name and version, or everything in a lockfile (`package-lock.json`,
Yarn classic and berry `yarn.lock`, `pnpm-lock.yaml`, `bun.lock`; packages for
other platforms included):

```bash
scripts/mirror-packages.sh                                     # everything examples/ needs
scripts/mirror-packages.sh express lodash@4.17.21 @types/node@22.10.0
scripts/mirror-packages.sh --lockfile ~/app/package-lock.json --lockfile ~/other/pnpm-lock.yaml
NOODLE_URL=https://npm.corp.example NOODLE_API_KEY=... CURL_OPTS="--cacert ca.crt" \
  scripts/mirror-packages.sh --lockfile package-lock.json
```

It keeps a download cache (`DOWNLOAD_DIR`), so the downloading and uploading
can happen on different machines: run it once where the internet is, carry
the cache over, and run it again (with `NOODLE_URL` set) where the server is.

### Migrating tarballs from an S3 bucket

`scripts/migrate-from-s3.py` copies every `.tgz` below a prefix of an S3
bucket into Noodle, once, for example a store laid out as
`npm/registry.npmjs.org/<name>/<file>.tgz` and
`npm/registry.npmjs.org/@scope/<name>/<file>.tgz` (the layout does not matter:
Noodle reads name and version from each tarball). It needs only Python 3.9+.

```bash
export AWS_ACCESS_KEY_ID=... AWS_SECRET_ACCESS_KEY=... NOODLE_API_KEY=...
scripts/migrate-from-s3.py \
    --source-url https://minio.corp.example:9000 --source-bucket packages \
    --source-prefix npm/registry.npmjs.org/ \
    --noodle-url https://npm.corp.example --parallel 64 \
    --state migration.sqlite --failures problems.tsv
```

* **Resumable.** Progress is kept in an SQLite file (`--state`). Ctrl-C
  finishes the uploads in flight and stops; after that, a crash or a `kill -9`,
  the same command continues where it stopped without listing the bucket
  again. Transient errors are retried; what still failed is retried on the
  next run.
* **Checked.** Each tarball is streamed from the GET straight into the upload
  (no temporary files), and the SHA-512 of the bytes read from S3 must match
  the integrity Noodle reports. Damaged tarballs (400) and different tarballs
  of an existing version (409) are recorded and written to `--failures`.
* **Fast.** The bucket is listed while uploading; `--parallel` uploads run at
  a time over kept-alive connections; `--noodle-url` can be repeated to spread
  the load over several instances. One copy of the script handles about 800
  small tarballs or 500 MB/s; for more, run copies side by side with
  `--shard 0/4`, `--shard 1/4`, ... and a `--state` file each. In a test with
  MinIO, Noodle and the script on one machine, Noodle took about 300 new
  packages, or 220 MB/s of large tarballs, a second; the store and the number
  of Noodle instances usually set the pace.
* **Catching up.** Tarballs added to the bucket after it was listed
  completely are copied with `--relist` (lists everything again, uploads only
  keys it has not seen), e.g. right before clients are switched over.
* `--list-only` lists the bucket and counts without uploading; `--limit N`
  copies at most N tarballs (for a trial run). Exit code 0 means every tarball
  is in Noodle, 1 that some were rejected, conflict or failed (or the run was
  interrupted), 2 that it could not run (settings, credentials, connection).

### dist-tags

Only `latest` is maintained automatically (the highest version without
prerelease, or the highest prerelease when there is no other). Other tags
(`next`, `beta`, `latest-4`) cannot be known from tarballs; set them with npm's
own command, using the API key as token:

```bash
npm dist-tag add express@4.22.3 latest-4 --//registry.npmjs.org/:_authToken="$KEY"
npm dist-tag rm express latest-4 --//registry.npmjs.org/:_authToken="$KEY"
```

or with curl (the body is the version as a JSON string):

```bash
curl -X PUT -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
     -d '"4.22.3"' "$NOODLE/-/package/express/dist-tags/latest-4"
curl -X DELETE -H "Authorization: Bearer $KEY" "$NOODLE/-/package/express/dist-tags/latest-4"
```

Tags set this way stay until they are removed; uploads never move them. A
`latest` set this way pins it; removing it brings back the automatic one.

### Deleting versions

```bash
curl -X DELETE -H "Authorization: Bearer $KEY" "$NOODLE/-/noodle/package/express/5.1.0"   # one version
curl -X DELETE -H "Authorization: Bearer $KEY" "$NOODLE/-/noodle/package/express"         # all of them
```

The version and its tarball are removed for good and everything derived from
the package is recomputed (`latest`, top-level fields, dist-tags pointing at
it). The same version can be uploaded again afterwards.

## Security advisories (npm audit)

`npm audit`, `yarn audit`, `yarn npm audit`, `pnpm audit` and `bun audit` work
offline once advisory data in [OSV](https://osv.dev) format has been imported.
Use OSV's export of the npm ecosystem (or a zip or tar.gz of the GitHub
Advisory Database):

```bash
# On a machine with internet access:
curl -fsSLO https://osv-vulnerabilities.storage.googleapis.com/npm/all.zip

# Then, next to the server:
curl -X PUT -H "Authorization: Bearer $KEY" --data-binary @all.zip \
     "$NOODLE/-/noodle/advisories?source=osv-npm-all.zip"
```

```json
{"imported":true,"importedAt":"2026-11-24T23:41:59.53Z","count":10777,"packages":3763,"source":"osv-npm-all.zip"}
```

Each import replaces the previous data, so import a fresh export now and then
(`WITH_ADVISORIES=1 scripts/mirror-packages.sh` downloads and imports it).
`/health` shows whether and when advisory data was imported. Advisories carry
the same severities, ranges, CWEs and CVSS scores as npm's own audit service;
their numbers are derived from the GHSA identifiers and their links point to
`https://github.com/advisories/GHSA-...`. Search results flag packages whose
latest version is affected (`flags.insecure`, `is:insecure`).

Before any data is imported, the audit endpoints answer `424 Failed
Dependency` with an explanation, so that clients report the audit as
unavailable instead of finding nothing; `npm install` itself is unaffected:

| Client | Output without advisory data |
|---|---|
| `npm audit` | `npm warn audit 424 Failed Dependency - POST .../advisories/bulk - No security advisory data has been imported into this registry yet ...`, exit code 1 |
| `yarn audit` (classic) | `Request "https://registry.yarnpkg.com/-/npm/v1/security/audits" returned a 424`, exit code 1 |
| `yarn npm audit` (berry) | `Response code 424 (Failed Dependency)`, exit code 1 |
| `pnpm audit` | `ERR_PNPM_AUDIT_BAD_RESPONSE The audit endpoint ... responded with 424: {"error":"No security advisory data ..."}` |
| `bun audit` | `error: POST .../advisories/bulk - 424`, exit code 1 |

`npm audit signatures` does not work offline: npm first downloads the
registry's signing keys from Sigstore's TUF repository
(`tuf-repo-cdn.sigstore.dev`) and gives up with `ENOTFOUND` after three
attempts. There would be nothing to verify anyway (see
[differences](#differences-from-registrynpmjsorg)).

## HTTP API

Read endpoints are anonymous; write endpoints (🔑) need the API key in
`Authorization: Bearer ...` or `X-Api-Key`. Scoped names can be written
`@scope%2fname` or `@scope/name`.

| Method | Path | Purpose |
|---|---|---|
| GET, HEAD | `/{name}` | Packument: abbreviated with `Accept: application/vnd.npm.install-v1+json`, full otherwise |
| GET, HEAD | `/{name}/{version}`, `/{name}/{dist-tag}` | One version manifest |
| GET, HEAD | `/{name}/-/{bare-name}-{version}.tgz` | Tarball |
| GET | `/-/package/{name}/dist-tags` | dist-tags |
| GET | `/-/v1/search?text=&size=&from=&quality=&popularity=&maintenance=` | Search |
| POST | `/-/npm/v1/security/advisories/bulk` | Audit (npm 7+, Yarn berry, bun) |
| POST | `/-/npm/v1/security/audits`, `/-/npm/v1/security/audits/quick` | Audit (npm 6, Yarn classic, pnpm) |
| GET | `/-/ping` | `{}` |
| GET | `/health`, `/health/ready` | Status, version, advisory data, counters; readiness (503 while loading) |
| PUT, POST 🔑 | `/-/noodle/upload` (`?wait=true`) | Upload a tarball |
| DELETE 🔑 | `/-/noodle/package/{name}[/{version}]` | Delete a version (or all of them) |
| PUT, DELETE 🔑 | `/-/package/{name}/dist-tags/{tag}` | Set or remove a dist-tag (`npm dist-tag`) |
| PUT, DELETE 🔑 | `/-/noodle/advisories` (`?source=`) | Import (replace) or remove advisory data |
| POST 🔑 | `/-/noodle/rebuild` (`?scope=catalog\|versions\|packuments`, `?wait=true`) | Rebuild from S3 |

Packuments come with `ETag`, `Last-Modified` and `Cache-Control: public,
max-age=300` and answer `If-None-Match` / `If-Modified-Since` with 304; they are
stored gzipped and sent as they are to clients that accept gzip. Unknown
packages are a `404 {"error":"Not found"}` answered from memory. The account
and publishing endpoints answer like a registry without accounts: `npm whoami`
gets a 401, `npm publish`, `npm deprecate` and `npm unpublish` a 405, and the
signing key and attestation endpoints a 404. Requests for paths Noodle does
not know are logged (category `Noodle.Unhandled`), so missing endpoints show
up when new clients are pointed at it.

See `Noodle.Api/Noodle.Api.http` for ready-made requests.

## Configuration

Settings come from `appsettings.json` or environment variables (`__`
separates levels).

| Environment variable | Default | Meaning |
|---|---|---|
| `Noodle__ApiKey` | *(none)* | API key for uploads and other writes. Without any key, writing is disabled. |
| `Noodle__ApiKeys__0`, `__1`, ... | | Additional keys (e.g. one per uploader). |
| `Noodle__PublicBaseUrl` | *(request host)* | Force the base URL of tarball URLs, e.g. `https://registry.npmjs.org`. |
| `Noodle__MaxTarballBytes` | `2147483648` | Largest accepted tarball. |
| `Noodle__MaxUnpackedBytes` | `8589934592` | Largest size a tarball may unpack to (gzip bombs). |
| `Noodle__PackumentCacheMegabytes` | `512` | Memory for cached packuments. |
| `Noodle__TarballCacheMegabytes` | `256` | Memory for cached small tarballs (`0` = off). |
| `Noodle__TarballCacheMaxKilobytes` | `1024` | Largest tarball kept in that cache. |
| `Noodle__LogRequests` | `true` | Log every request. |
| `Noodle__Ingest__BatchDelayMilliseconds` | `500` | How long a busy package gathers versions before it is rewritten. |
| `Noodle__Ingest__BusyWindowMilliseconds` | `2000` | A package counts as busy (uploads are batched) this long after its last commit. |
| `Noodle__Ingest__RecoverAfterSeconds` | `60` | Acknowledged uploads not committed after this long (the instance stopped) are committed by another instance. |
| `Noodle__Sync__PollIntervalSeconds` | `5` | How often other instances' changes are picked up. |
| `Noodle__Sync__SnapshotIntervalMinutes` | `15` | How often the catalog snapshot is rewritten (when something changed). |
| `Noodle__Sync__DownloadFlushSeconds` | `60` | How often download counts are saved. |
| `Noodle__Sync__ChangeRetentionHours` | `24` | How long change log entries are kept after a snapshot includes them. |
| `Noodle__Sync__RebuildParallelism` | `32` | Parallel S3 reads during rebuilds. |
| `Storage__ServiceUrl` | *(AWS)* | S3 endpoint, e.g. `https://minio.corp.example:9000`. |
| `Storage__Bucket` | `npm` | Bucket. |
| `Storage__Prefix` | *(none)* | Key prefix, to share a bucket. |
| `Storage__Region` | `us-east-1` | Region (signing region for non-AWS stores). |
| `Storage__AccessKey` / `__SecretKey` | *(credential chain)* | Static credentials; otherwise the AWS default chain (environment, profile, IRSA). |
| `Storage__ForcePathStyle` | `true` | `host/bucket/key` URLs (needed by MinIO and most non-AWS stores). |
| `Storage__ConditionalWrites` | `true` | Use `If-Match` / `If-None-Match` (see [Operations](#operations)). |
| `Storage__CreateBucket` | `true` | Create the bucket when it is missing. |
| `Storage__UseChunkEncoding` | `true` | `aws-chunked` uploads; disable for stores that reject them. |
| `Storage__MaxErrorRetry` | `3` | Retries of the S3 client itself for transient errors (Noodle retries a few more times on top). |
| `ASPNETCORE_HTTPS_PORTS` | | HTTPS port(s), e.g. `8443`. |
| `ASPNETCORE_Kestrel__Certificates__Default__Path` / `__KeyPath` | | TLS certificate and key (PEM). |
| `SSL_CERT_DIR` | `/etc/ssl/certs` | Extra trusted CA directories, e.g. `/etc/ssl/certs:/certs/ca`. |

**Internal CA for S3**: the S3 client checks MinIO's certificate against the
OS trust store. Either bake the CA into the image (see the comment in
`Noodle.Api/Dockerfile`: copy it to `/usr/local/share/ca-certificates/` and run
`update-ca-certificates`) or mount it and add its directory to `SSL_CERT_DIR`
(as `deploy/docker-compose.yml` does). Without it, Noodle logs "The SSL
connection could not be established" and keeps retrying.

## How it works

Everything lives in the bucket:

```
tarballs/{name}/{version}.tgz                  the tarball, byte for byte
versions/{name}/{version}.json.gz              manifest, readme and arrival time of one version
packages/{name}.json.gz                        all versions of one package: the commit point
packuments/{name}/{hash}.{corgi|full}.json.gz  generated packuments, named by their content hash
pending/{name}/{version}                       uploads acknowledged but not yet committed
catalog/snapshot.bin.gz                        compact catalog of all packages, for fast startup
changes/{ticks}-{instance}-{kind}-{subject}    small change log shared by instances
advisories/advisories.json.gz                  imported security advisories
```

* **Upload**: the tarball is read completely (gzip and tar, with size limits;
  links and special entries are ignored, nothing is executed), `package.json`
  is normalized like `npm publish` does it (`@npmcli/package-json`'s fix and
  prepare steps: `bin` and `bundleDependencies` forms, `scripts.install` for
  `binding.gyp`, `readme`, people, `repository`, `bugs` and `homepage` via
  hosted-git-info, ...), and the tarball and the version document are written
  with `If-None-Match`, so the first tarball of a version wins.
* **Commit**: the package document is updated with `If-Match` on its previous
  ETag (retried when another writer got there first). It names the current
  packuments, which are written just before it under their content hash;
  nothing is ever visible half done. Versions of one package that arrive
  together are committed together.
* **Serving**: every instance keeps a catalog of all packages, versions,
  dist-tags and search fields in memory (one million packages with ten million
  versions take about 690 MiB). It answers search, dist-tags and unknown names
  without S3 and knows the current packument of each package; packuments and
  small tarballs are cached in memory too, and since packument objects never
  change, cached copies need no revalidation.
* **Several instances** learn about each other's changes by polling the
  change log (one `ListObjectsV2` request every few seconds). Snapshots of the
  catalog are written periodically by whichever instance gets there first,
  and old change log entries are removed.

## Operations

* **Multiple instances**: run as many as you like behind a load balancer
  against the same bucket; no sticky sessions needed. Uploads through one
  instance appear on the others within `PollIntervalSeconds`. This relies on
  the store honouring conditional writes (`If-Match` / `If-None-Match` on
  `PutObject` and `CompleteMultipartUpload`); MinIO, RustFS and AWS S3 do.
  Check yours: the second of these must fail with `PreconditionFailed`:

  ```bash
  aws --endpoint-url "$S3_URL" s3api put-object --bucket npm --key probe --body README.md --if-none-match '*'
  aws --endpoint-url "$S3_URL" s3api put-object --bucket npm --key probe --body README.md --if-none-match '*'
  aws --endpoint-url "$S3_URL" s3api delete-object --bucket npm --key probe
  ```

  If it does not, set `Storage__ConditionalWrites=false` and run a **single**
  instance.
* **Startup**: `/health/ready` returns 503 until the catalog is loaded (about 3
  seconds for a million packages); installs already work meanwhile, from S3.
  Search answers 503 until then.
* **Recovery**: the tarballs and version documents are the source of truth;
  everything else can be derived from them. A missing or damaged snapshot is
  rebuilt from the package documents automatically. Uploads acknowledged by an
  instance that stopped before committing them are committed by another
  instance after `RecoverAfterSeconds`. `POST /-/noodle/rebuild` rebuilds the
  catalog from the package documents; `?scope=versions` also recreates package
  documents from the version documents, `?scope=packuments` also regenerates
  every packument (after an upgrade or a change of `PublicBaseUrl`). Rebuilds
  run in the background; `/health` shows their progress.
* **Backups**: back up (or replicate) the bucket. Nothing else holds state.
* **Monitoring**: `/health` reports the version, the number of packages, the
  advisory data status and counters (commits, retried write races, cache hits,
  S3 requests by kind).
* **Storage**: Noodle retries S3 requests that fail transiently (busy store,
  broken connection). MinIO on drives with slow `fsync` stalls now and then for
  its drive timeout (30 seconds); uploads still succeed, just later.
* **Tested with** MinIO (also over TLS with an internal CA), RustFS, and npm
  11, Yarn 1.22 and 4.12, pnpm 10 and bun 1.4 installing a real project on a
  network without internet access, with lockfiles created online. MinIO's
  open-source server is no longer maintained upstream; S3 compatible
  alternatives work too.

## Differences from registry.npmjs.org

Noodle only has the tarballs, so whatever the npm registry knows from other
sources is missing or approximated:

* **dist-tags**: only `latest` is automatic, and it follows the highest
  version, not the most recently uploaded one. Tags such as `next`, `beta` or
  `latest-4` exist only when set (see [dist-tags](#dist-tags)).
* **Times are arrival times.** `time[version]` is when the tarball arrived,
  `time.created` the earliest arrival of the present versions,
  `time.modified` the last change. `npm view ... time`,
  `npm install --before`, pnpm's `minimumReleaseAge` and Yarn's
  `npmMinimalAgeGate` therefore see arrival times: an old version that just
  arrived looks brand new, and is held back by minimum-age settings until it
  is as old as they require.
* **No deprecations.** `npm deprecate` messages live in the registry, not in
  the tarballs, so installs show no deprecation warnings.
* **No registry signatures or provenance.** `dist.signatures` and
  `dist.attestations` are never present, and no signing keys are published;
  `npm audit signatures` fails (see above).
* **Local statistics.** Download counts are this registry's own (they rank
  search results), `dependents` is 0, and scores are Noodle's.
* **No accounts.** `maintainers`, `_npmUser` and the search result's
  `publisher` come from `package.json` (`maintainers`, `author`), not from npm
  accounts; publishing and logging in are not possible.
* **Manifests are regenerated the way current npm publishes.** Fields the npm
  client or registry adds at publish time cannot be known (`_npmUser`,
  `_npmVersion`, `_nodeVersion`, `gitHead`, `_npmOperationalInternal`), and
  versions published by old npm releases or by other clients were normalized
  differently there (e.g. `bin` paths with `./`, `repository` URLs not
  rewritten, no `bugs`/`homepage` derived, `readme` per version). Noodle adds
  `fileCount` and `unpackedSize` for old versions that lack them. In a
  comparison of 75 real versions, the abbreviated documents installs use were
  identical apart from these, deprecations, and two cases npm decided from
  files that are not in the tarball (an install script for a `binding.gyp`
  left out of `fsevents@2.3.3`, `contributors` from an unpacked `AUTHORS`
  file).
* **Readmes** come from the tarball (capped at 512 KiB) and are shown for the
  latest version.
* **Search** uses Noodle's own ranking over names, descriptions, keywords and
  people; results and totals differ from the npm website's.

## Development

```bash
dotnet test                       # 132 tests, in-memory S3; no services needed
```

Run the server against a local S3 server (any S3 compatible one), e.g.:

```bash
docker run -d -p 9000:9000 -e RUSTFS_ACCESS_KEY=minioadmin \
  -e RUSTFS_SECRET_KEY=minioadmin rustfs/rustfs
dotnet run --project Noodle.Api   # Development: http://localhost:5090, API key "dev-api-key"
```

`appsettings.Development.json` points at `http://localhost:9000` with
`minioadmin` credentials. Then fill it and install a project from it:

```bash
scripts/mirror-packages.sh        # everything examples/package-lock.json needs
cd examples
npm ci                            # through examples/.npmrc
npm run build && npm start        # http://localhost:3000
```

`examples/.npmrc` points npm at the development server and uses its own cache
(`examples/.npm-cache`), so every package really comes from Noodle. Copy it
next to any project to test that project against the server.

Build the image with `docker build -f Noodle.Api/Dockerfile .` (the tests run
during the build; `--build-arg SKIP_TESTS=true` skips them).

### Releases

Publishing a GitHub release with a tag like `v0.1.0` runs
`.github/workflows/release.yml`: it runs the tests, builds the image for
`linux/amd64` and `linux/arm64` and pushes it to
`ghcr.io/linus-berg/noodle/api` as `0.1.0`, `0.1` and `latest` (`1` from v1.0.0
on; prereleases such as `v0.2.0-rc.1` only get their own tag). The version is
stamped into the build; it is logged at startup and reported by `/health`.

Code style follows `Noodle.sln.DotSettings` (Rider / ReSharper).
