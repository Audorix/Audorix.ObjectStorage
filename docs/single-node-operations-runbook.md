# Single-Node Operations Runbook

## Start, status, and stop

Set the non-secret names and externally managed secrets from `.env.example`. Run `Build.ps1`, then run `Provision.ps1` once in the approved provisioning window. Provisioning creates only the configured marker/schema/bucket and is idempotent; it is intentionally separate from restart. Use `Start.ps1` afterward. `Status.ps1` checks recorded container ownership labels and readiness. `Stop.ps1` refuses to stop anything whose recorded container/project ownership differs; it never kills by port.

Liveness proves only that the process can answer. Readiness verifies the persistent identity marker and configured bucket/database path. `/ops/metrics` requires the separate operations bearer token and reports aggregate active/completed/failure/rejection/latency, process memory, free disk, and staging counts without keys, credentials, connection strings, or internal paths.

Alert when readiness fails; any unexpected 5xx occurs; rejections persist outside an understood load event; active operations do not drain during maintenance; p95 exceeds the application objective for 15 minutes; PostgreSQL pool use exceeds 75%; working set exceeds 80% of 1 GiB; free disk falls below max(2 GiB, twice the largest permitted upload plus expected staging); staging remains after requests drain; or the latest valid off-host backup is older than 25 hours. Drain traffic, preserve logs/metrics, check database and disk independently, and avoid automatic failover to another blob root.

## Backup and restore

`Backup.ps1 -Name <set>` gracefully stops the owned container, creates and validates the paired PostgreSQL/blob set, then restarts and waits for readiness. Copy the set to protected, authenticated off-host storage; the manifest checks consistency, not authenticity or encryption. Enforce retention outside this container.

For a restore, create a new empty database/schema identity and supply `AUDORIX_RESTORE_POSTGRES_CONNECTION`. `Restore.ps1 -BackupName <set> -TargetVolume <new-volume>` refuses an existing target volume, restores into a separate database and blob location, reconciles them, and leaves source activation unchanged. Verify with `AUDORIX_BLOB_VOLUME=<new-volume>` and `AUDORIX_STORAGE_ROOT=/var/lib/audorix/data` in an isolated project/port before promotion. Never restore over the active schema or root.

## Replacement and rollback

During replacement, stop the old container and wait for lock release before starting the new one. Overlapped recycling is unsupported. Preserve the database, blob, Data Protection, and backup volumes. Run readiness, CLI PUT/HEAD/GET/list/range/delete, negative credential, and integrity checks after replacement.

Rollback requires the prior immutable image digest and unchanged compatible data. `Rollback.ps1` performs ownership-checked stop/start only. If a release changed metadata or blob format, follow that release's migration rollback plan instead; never point an older image at data without compatibility evidence. Record who approved, executed, verified, and can reverse each deployment.

## Intended-server validation

On the authorized server, record image/base-image digests and assembly hashes; verify filesystem ownership and free space; validate TLS and exact SigV4 host/path/query preservation from a separate client; run `Validate.ps1`; test invalid credentials and cross-bucket scope; exercise max-size and admission rejection; replace the container and hash-check retained files; perform separate backup/restore; run a representative bounded workload while recording CPU, memory, disk, pool, latency, failures, and staging; then verify rollback. Do not call the server validated until every executed check is captured in `deployment-validation-summary.json`.
