# Intended-Server Handoff

Complete `deployment/single-node/intended-server-inputs.example.yaml` and obtain explicit change authorization before connecting to a server. Supply secret values from the target secret manager into process environment variables; the template contains references only.

## Preflight

```powershell
docker version
docker compose version
docker image inspect audorix-objectstorage@sha256:ed51fa14ec1fdaec2aa615fa3313c4f986d27806b3b6059bc5ff4aeddfa178b3
Get-Volume
Test-NetConnection <postgres-host> -Port 5432
```

Confirm local persistent filesystem ownership `10001:10001`, capacity plus 2 GiB reserve, PostgreSQL TLS verification and role separation, approved bind/DNS/firewall, certificate path, trusted proxy IPs, monitoring, backup retention, maintenance owner, and prior rollback digest. Set `AUDORIX_COMPOSE_PROJECT`, all non-secret `AUDORIX_*` values, and secrets from their approved providers. For direct Kestrel TLS set `AUDORIX_COMPOSE_TLS=true`, `AUDORIX_TLS_CERTIFICATE_HOST_PATH`, and `AUDORIX_TLS_CERTIFICATE_PASSWORD`. For a proxy, keep the private profile and configure only explicit `Network__KnownProxies__N` values.

## Deploy and validate

```powershell
deployment\single-node\scripts\Build.ps1
deployment\single-node\scripts\Provision.ps1   # exactly once, approved window
deployment\single-node\scripts\Start.ps1
deployment\single-node\scripts\Status.ps1
deployment\single-node\scripts\Validate.ps1
```

From a separate authorized client, repeat SDK and CLI validation using the real DNS name and CA trust. Test invalid credentials, cross-bucket denial, 64 MiB plus one byte rejection, trusted/untrusted forwarded headers, replacement persistence, and resource thresholds. Record image and assembly hashes plus all outcomes. Do not call the target validated from process liveness alone.

## Backup and rollback

```powershell
deployment\single-node\scripts\Backup.ps1 -Name pre-deploy-<UTC_TIMESTAMP>
deployment\single-node\scripts\Restore.ps1 -BackupName pre-deploy-<UTC_TIMESTAMP> -TargetVolume <new-validation-volume>
deployment\single-node\scripts\Rollback.ps1 -PriorImage '<repository>@sha256:<approved-prior-digest>'
```

Restore requires a separate empty database/schema through `AUDORIX_RESTORE_POSTGRES_CONNECTION`; never restore over active data. Rollback is permitted only with format-compatible data and explicit approval.

## Cleanup

If validation fails, collect redacted logs/metrics, run ownership-checked `Stop.ps1`, and use the exact project plus Compose files to remove only newly created containers. Remove volumes only after the owner confirms they contain no required data and a valid backup exists. Never kill by port, run `down --volumes` against production data, alter DNS/firewalls, or delete the previous image without explicit approval.

No intended server or authorized access method was supplied in this slice. Deployment remains pending on the completed input template and authorization.
