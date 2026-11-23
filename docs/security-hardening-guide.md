# Security Hardening Guide

Use this as a deployment gate for the supported single-instance PostgreSQL/filesystem topology.

## Host and Network

1. Require TLS. Preserve the exact externally signed host, escaped path, and query string through any reverse proxy.
2. Configure Forwarded Headers with explicit `KnownProxies`/`KnownNetworks` and a forward limit. Never trust arbitrary client `X-Forwarded-*` values.
3. Set `AllowedHosts` to deployed names. Keep S3 routes out of body logging and redact `Authorization`, cookies, `x-amz-*`, continuation tokens, connection strings, and secrets.
4. Supply credentials, connection strings, and signing keys through a secret store or protected environment/config provider. Never put them in source, `appsettings`, `database.json`, packages, command lines, or support bundles.
5. Give each S3 caller only required buckets and operations. Rotation/revocation requires replacement configuration and process restart.
6. Configure CORS narrowly or leave it disabled. Cookie-authenticated hosts must apply their CSRF policy to state-changing routes.

## Bounded Profile

The sample host explicitly uses:

```csharp
options.MaxObjectSize = 512L * 1024 * 1024;
options.MaxKeyLengthBytes = 1024;
options.MaxQueryStringBytes = 8 * 1024;
options.MaxHeaderCount = 100;
options.MaxHeaderBytes = 32 * 1024;
options.MaxContinuationTokenLength = 8 * 1024;
options.ContinuationTokenLifetime = TimeSpan.FromMinutes(15);
options.MaxConcurrentOperations = 32;
options.MaxQueuedOperations = 8;
```

Core defaults also cap metadata at 50 entries and 8 KiB combined UTF-8 names/values, content type at 256 characters, and keys at 1024 UTF-8 bytes. Lists remain capped at 1000 results.

Tune concurrency below file-handle, PostgreSQL pool, and disk limits. A zero queue rejects immediately; keep any queue small. Configure matching Kestrel `MaxRequestHeaderCount`, `MaxRequestHeadersTotalSize`, `RequestHeadersTimeout`, `KeepAliveTimeout`, connection/stream limits, and suitable `MinRequestBodyDataRate`. Apply proxy connection, body, rate, and idle limits too.

Upload enforcement uses `IHttpMaxRequestBodySizeFeature` plus streaming hash validation, including unknown content length. It never buffers a complete object. Cancellation flows through HTTP, streams, and PostgreSQL. Monitor overload responses, `413`, cancellations, staging growth, pool saturation, file handles, and disk.

## Filesystem

- Put the root outside web content on persistent local storage with stable locking and same-volume atomic rename behavior.
- Grant the service identity read/write/create/delete only on the root. Deny shared/interative users. Do not expose blobs, staging, locks, Data Protection keys, or backups through static files.
- Keep all path ancestors under trusted ownership. Prevent untrusted symlinks, junctions, reparse points, or mount substitutions.
- Run exactly one engine per root. Recycling must stop the old process and release its lock before replacement traffic.
- Reserve capacity for staging plus committed blobs and schedule cleanup after the retention window.

Keys are not filesystem paths. Do not normalize valid `../`, slash, backslash, Unicode, or special-character key identity as a traversal defense.

## PostgreSQL

- Require TLS and certificate validation for remote PostgreSQL.
- Limit runtime permissions to connect, schema usage, and required table/sequence DML. Avoid superuser, database owner, role creation, and unrelated schemas.
- Use separate provisioning/migration and backup/restore roles when practical. The provider currently initializes missing schema/tables, so test startup before removing create rights.
- Align Npgsql maximum pool size and timeouts with HTTP admission. Monitor locks, long statements, and pool exhaustion.
- Keep `pg_dump`/`pg_restore` paths administrator-controlled; request data must never select executables.

## Data Protection and Content

Persist Data Protection keys to a durable protected directory or managed repository with a stable application name. Restrict access to the service identity and protect keys at rest. Losing keys invalidates cookies/API tokens; sharing keys enlarges the trust boundary.

Use a separate S3 continuation signing key of at least 32 UTF-8 bytes. Rotation invalidates outstanding S3 tokens. Keep the inline type allowlist narrow. Add CSP, frame-ancestor policy, malware scanning, content validation, and download-domain isolation according to product risk.

## Backup and Restore

- Back up to a new operator-controlled directory with restrictive permissions. Sets contain plaintext content and metadata unless the storage layer encrypts them.
- The manifest verifies consistency, not authenticity. Use authenticated transfer and organization-level encryption/signing off-host.
- Never validate/restore from an untrusted-writable directory. Reparse points are outside the backup trust model.
- Restore only to a nonexistent root and absent schema with isolated credentials. Test restore without overwriting production.

## Release Gate

Run Release regressions, real PostgreSQL/Kestrel adversarial tests, AWS SDK and available CLI compatibility, package-only consumption, advisory checks, artifact secret scans, a bounded mixed workload, and a fresh long soak for the exact binaries. Verify ReportStudio's Production authentication boundary and the deployed proxy/TLS path. The rc.1 two-hour soak does not qualify rc.2.
