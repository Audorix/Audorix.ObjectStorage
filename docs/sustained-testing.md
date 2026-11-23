# Sustained Mixed Workload

The existing load harness has a duration-based mode that starts an isolated Kestrel process, PostgreSQL schema, filesystem root, and bucket. Each worker repeatedly performs upload, full read with SHA-256 verification, ListObjectsV2 verification, a 4 KiB-or-smaller range verification, and delete.

Short local validation:

```powershell
dotnet run --project tools/Audorix.ObjectStorage.LoadHarness -c Release -- `
  --duration-seconds 60 `
  --concurrency 4 `
  --object-kib 64 `
  --output docs/sustained-results.json
```

Longer intended-server run, executed from the checked-out source on that server without deploying the pilot:

```powershell
$env:AUDORIX_OBJECTSTORAGE_POSTGRES = '<isolated-test-database-connection>'
dotnet run --project tools/Audorix.ObjectStorage.LoadHarness -c Release -- `
  --duration-seconds 21600 `
  --concurrency 16 `
  --object-kib 256 `
  --work D:\AudorixSoak `
  --output D:\AudorixSoak\six-hour-results.json
```

Use a non-production PostgreSQL database and a persistent work volume with enough free space. The harness creates a unique `audorix_sustained_*` schema and `audorix-sustained-*` directory, removes only those resources, and returns nonzero when any operation or integrity check fails. The result records requested and actual duration, concurrency, object size, attempted and completed operations, verified hashes, approximate transferred bytes, failures, server resource endpoints, and minimum/P50/P95/P99/maximum latency by operation.

The current short rc.2 evidence is [twelfth-sustained-results.json](twelfth-sustained-results.json). The package-only two-hour workflow is run with `soak/Launch.ps1`, monitored with `soak/Status.ps1`, and stopped only through `soak/Stop.ps1`. A short validation is not a long-soak or capacity guarantee, and results from rc.1 must not be transferred to rc.2.
