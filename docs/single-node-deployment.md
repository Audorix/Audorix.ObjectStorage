# Single-Node Deployment

## Scope and identities

This bundle deploys one Linux/Kestrel process backed by one PostgreSQL schema and one persistent local filesystem volume. It is package-only: the host references `Audorix.ObjectStorage`, `Audorix.ObjectStorage.PostgreSql`, and `Audorix.ObjectStorage.S3` `0.1.0-rc.2` from `artifacts/candidates/4c5bbe9/packages`. It does not compile shipping projects. A shared database does not make filesystem blobs multi-node capable.

The candidate source is `4c5bbe953687023018eb3d2ca230f75e4e6f6c94`. Package hashes are in `artifacts/candidates/4c5bbe9/release-manifest.json`; `scripts/Build.ps1` verifies them before building. The validated local image is `audorix-objectstorage@sha256:ed51fa14ec1fdaec2aa615fa3313c4f986d27806b3b6059bc5ff4aeddfa178b3`. Pin that digest when promoting an exported or registry-hosted image.

## Required inputs

Copy the names from `deployment/single-node/.env.example` into the target secret/configuration facility. Never commit the resulting values. Required target approval inputs are: server and operator identity, Docker/Compose versions, external PostgreSQL host/database/schema and runtime/provisioning/backup roles, blob/Data Protection/backup volume locations and ownership, private bind address or authorized DNS name, TLS certificate source, trusted proxy IPs, firewall policy, backup destination/retention, monitoring destination, maintenance window, and rollback image digest. No target or authorization was supplied for this slice.

Build with `scripts/Build.ps1`. The default `compose.yaml` binds HTTP to loopback and is suitable behind an authorized same-host proxy. Use `compose.tls.yaml` as a second Compose file for direct Kestrel TLS. It requires a read-only PFX mount and externally supplied password. There is no Nginx or Cloudflare dependency. Cloudflare remains optional and unverified.

The application runs as UID/GID `10001`, with all capabilities dropped, no-new-privileges, a read-only root filesystem, a bounded 64 MiB `/tmp`, persistent blob and Data Protection volumes, 1 GiB memory, 2 CPUs, and 256 PIDs. Initial named volumes inherit application ownership; restore uses a root one-shot solely to assign a new empty volume to UID/GID 10001. The long-running process is never root.

## Network correctness

The private profile exposes only `127.0.0.1` by default. Do not change the bind address, DNS, firewall, or proxy without explicit authorization. TLS is required for non-loopback traffic. A proxy must preserve the externally signed `Host`, escaped path, raw query, and signed headers. Configure `Network__KnownProxies__0` etc. only with explicit proxy IPs; arbitrary forwarded headers are not trusted. Keep database credentials separate from S3 credentials. The runtime PostgreSQL role should have only connect, schema usage, table/sequence DML, while provisioning and backup/restore use separate roles where supported.

## Startup guards and limits

Provisioning is explicit. Normal startup requires the expected identity marker in the blob volume and the expected bucket in PostgreSQL, otherwise startup fails instead of accepting an empty mount or different database. The filesystem root must support stable exclusive locks and same-volume rename. Never run two storage containers against the same root.

The local starting envelope is provisional: 64 MiB objects, 8 active and 4 queued operations, 1,024-byte UTF-8 keys, 50 metadata entries/8 KiB metadata, 100 headers/32 KiB total headers, 8 KiB query and continuation token, 15-second request-header timeout, 120-second keep-alive, Npgsql pool maximum 16, 1 GiB memory, 2 CPUs, and 2 GiB free-disk reserve above retained plus staging demand. The 64 MiB size and 8/4 envelope derive from thirteenth-slice local measurements; container CPU/memory, timeouts, pool size, disk reserve, and backup retention are conservative provisional settings to remeasure on the intended server. Start with daily paired backups retained 14 days unless organizational policy requires more.

## Reproducible local profile

`compose.local-postgres.yaml` adds isolated PostgreSQL 18.3 for local validation only. Supply all secrets through the process environment, start its `local-postgres` profile, run `Provision.ps1` exactly once, then run `Start.ps1` and `Validate.ps1`. Do not use this profile to reset or replace an existing database. The validation endpoint is `http://127.0.0.1:5195/s3`; readiness is `/health/ready` and liveness is `/health/live`.
