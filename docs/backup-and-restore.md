# PostgreSQL and Blob Backup Procedure

## Supported Consistency Model

This is a quiesced backup. `PostgreSqlObjectStorageBackupService.CreateAsync` enters engine maintenance, rejects new mutations, drains admitted mutations, blocks cleanup, captures the PostgreSQL schema and every referenced blob, validates the result, and only then publishes the destination directory. Maintenance is released in success, cancellation, and failure paths.

All writers and cleanup must use the same engine instance and blob root. Direct database writers, filesystem writers, another host, or other external actors are unsupported during backup. Stop those actors before creating the set. This is not an online multi-node backup.

Register the service:

```csharp
builder.Services.AddAudorixObjectStoragePostgreSqlBackup(options =>
{
    options.PgDumpPath = "/path/to/pg_dump";
    options.PgRestorePath = "/path/to/pg_restore";
});
```

Create a destination that does not already exist:

```csharp
ObjectStorageBackupManifest manifest =
    await backupService.CreateAsync("/backups/audorix-2026-09-29", cancellationToken);
```

Incomplete work uses a sibling `.audorix-backup-<id>.incomplete` directory and is never accepted without a complete manifest. A failed creation removes its temporary set where possible.

## Backup Contents

- `manifest.json`: format version, metadata schema version, unique set ID, schema name, UTC timestamps, dump descriptor, inventory descriptor, and object count.
- `metadata.dump`: PostgreSQL custom-format schema dump.
- `inventory.ndjson`: ordered, streaming blob inventory.
- `blobs/<blob-id>.blob`: referenced object bytes.

The dump, inventory, and every blob have SHA-256 and length records. Every inventory entry is bound to the backup-set ID. This detects accidental corruption, truncation, unsafe paths, duplicates, and mixed files. It is not tamper-proof authentication: an attacker able to rewrite both data and manifest can recompute checksums. Protect backup storage with operating-system access controls or add an external signature.

## Validation

```csharp
BackupValidationResult result =
    await backupService.ValidateAsync("/backups/audorix-2026-09-29", cancellationToken);
```

Validation streams the inventory and blobs, verifies lengths and SHA-256 values, requires strictly ordered unique safe blob IDs/paths, verifies supported versions and completion state, and asks `pg_restore --list` to parse the dump archive.

## Isolated Restore

Restore never overwrites an existing root or schema. The schema named in the backup must be absent in the target connection, and the destination root must not exist:

```csharp
await backupService.RestoreAsync(
    "/backups/audorix-2026-09-29",
    "/restore-candidates/audorix-2026-09-29",
    isolatedTargetConnectionString,
    cancellationToken);
```

The service validates before database execution, restores into the absent schema, copies blobs into an unpublished temporary root, reconciles restored database references against the ordered inventory, and then publishes the root. Failure removes the candidate schema and temporary root; it does not modify the active schema or root.

Use a separate database or an otherwise isolated target when the source schema still exists. After application-level verification, activation is an explicit operator action outside this library.

## Large-Transfer Harness

The harness is intentionally outside the normal test suite:

```powershell
dotnet run --project tools/Audorix.ObjectStorage.LoadHarness/Audorix.ObjectStorage.LoadHarness.csproj -c Release -- --comparison-mib 64 --size-mib 1024 --concurrency 1,4 --output D:\measurements\audorix-object-storage.json
```

It reads the same `database.json` fallback or `AUDORIX_OBJECTSTORAGE_POSTGRES`, starts Kestrel in a child process, uses isolated schema/root resources, performs an 8 MiB warm-up, samples every 100 ms, verifies incremental SHA-256 and lengths, tests cancelled-overwrite preservation, checks staging cleanup, and removes temporary resources. The connection string is passed only through the child process environment and is not written to results.
