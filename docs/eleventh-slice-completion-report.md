# Eleventh Slice Completion Report

Date: 2026-09-30

## Result

The pinned `0.1.0-rc.1` candidate passed a two-hour mixed-workload soak, graceful restart verification, quiesced backup validation, isolated restore, restored-host SDK verification, and an original-dataset recheck. This was local validation. No authorized intended deployment server was already available, so the intended-server gate remains pending.

The RC is ready for a controlled pilot in the tested local, single-instance environment. This result does not certify shared hosting, multi-node storage, abrupt process loss, power-loss recovery, or production capacity.

## Source and package identity

- Candidate package source: `9bbf4e4203cdb387d2ce66f966f6410f62b145f4`.
- Package version: `0.1.0-rc.1`; no packed library code changed in this slice.
- Package-pinned harness source: `b6c3f027f9ac4368e65a4b356abfed0a921d4419`.
- The harness contains package references only for Audorix libraries and restores them from `artifacts/candidates/9bbf4e4203cd/packages` using an isolated cache.
- Candidate hashes were checked before launch and recorded in `docs/eleventh-soak-summary.json`.
- Loaded binary SHA-256 values were `CC047E...F273` (core), `F5E668...E23D8` (PostgreSQL), and `83A8A2...7570` (S3). All three hosts used harness executable SHA-256 `D60602...1280`.

The original RC artifacts were preserved. No new prerelease was produced because the demonstrated fixes were confined to validation tooling and did not alter package contents.

## Environment and isolation

The run used Windows `10.0.26200` x64, .NET runtime `10.0.12`, 20 logical processors, PostgreSQL server/client `18.3`, and approximately 692.5 GB initial free disk. The storage host used loopback HTTP and a local PostgreSQL connection; no external network hop was exercised.

Run `20260930165214-8a679426` received a unique source schema, bucket, credentials, blob root, restore database, and restored blob root. Secrets remained in process environments and were not written to reports or command lines. The existing browser pilot used different processes, ports, schema, bucket, and roots.

After the soak cleanup, the existing pilot remained owned and ready at storage `5185`, ReportStudio API `5165`, and frontend `5273`. A real-browser check still showed the reviewer-scoped Northwind Pilot workspace and `Private brief.pdf`.

The run used four workers, 64 KiB structured/small objects, periodic 4 MiB objects, a 1 GiB run-owned disk limit, five-second unreferenced-blob retention, two-second cleanup cadence, and one-minute checkpoints.

## Acceptance budgets

Budgets were fixed before the definitive run: zero unexpected failures and retries; data-operation p95 at or below 2 seconds; cleanup p95 at or below 10 seconds; controlled-cancellation p95 at or below 5 seconds; managed heap below 512 MiB; working set and run-owned disk below 1 GiB; PostgreSQL connections below 32; and zero missing, unreferenced, or staging files after drain.

The definitive run passed every budget. Earlier rehearsals are not combined with its results. One interrupted rehearsal exposed unbounded deferred blob accumulation under overwrite churn, so the harness began invoking the existing cleanup API periodically. Later rehearsals exposed readiness-file, telemetry-enumeration, and empty-page handling races. Those harness-only defects were fixed and rehearsed before the full clock restarted from zero.

## Workload results

- Start: `2026-09-30T18:52:15.492387+02:00`.
- End: `2026-09-30T20:52:55.7866739+02:00`.
- Actual workload: `02:00:00.0073279`; total wall time including recovery work: 7,240.294 seconds.
- Completed operations: 222,004.
- Integrity checks: 143,944.
- Expected controlled cancellations: 2,355.
- Expected authorization denials: 2.
- Unexpected failures: 0. Explicit retries: 0; AWS SDK `MaxErrorRetry` was zero.
- Uploaded: 12,809,273,344 bytes. Downloaded: 19,383,393,011 bytes.
- Sampled average: 30.61 operations/second and 4.22 MiB/second combined upload/download traffic.

P95 latency was 1,253 ms upload, 153 ms full read, 3 ms metadata, 198 ms paginated listing, 281/298/274 ms explicit/open/suffix range, 1,307 ms overwrite, 147 ms overwrite verification, 152 ms delete, 1,582 ms controlled cancellation, and 5,344 ms cleanup. Individual maxima were retained in the JSON evidence and were not hidden by retries.

The workload exercised streamed upload/download, HEAD metadata/existence, prefix listing with three-item pagination, explicit/open-ended/suffix ranges, overwrite, delete, and cancellation of throttled overwrites. Complete reads used SHA-256; ranges were compared byte-for-byte.

## Resource observations

Across 119 one-minute samples, maximum storage-host managed heap was 25,716,320 bytes and working set was 117,788,672 bytes. Maximum sampled run-owned disk was 54,598,922 bytes. Up to four staging files were visible during active concurrent uploads; final staging count was zero. Maximum observed PostgreSQL connections from the host were 1 of 100 configured, and maximum sampled database size was 10,041,023 bytes.

One-minute resource sampling can miss shorter peaks. Managed heap and working set cover the storage host, not PostgreSQL or total machine memory. P50/P95 values use one-millisecond histogram buckets; observed maxima retain their measured fractional values.

## Reconciliation and restart

After the workload drained, transient keys were removed, the configured retention interval elapsed, and cleanup ran. Source reconciliation found 16 referenced blobs, 16 physical blobs, no missing or unreferenced blobs, and no staging files.

The source host was stopped gracefully through its owned process and restarted against the same schema/root. Retained lengths, metadata, complete hashes, paginated listing, ranges, and invalid-credential denial were rechecked successfully. This is graceful restart evidence only; it is not an abrupt-host-loss or power-loss claim.

## Backup and restore

The quiesced backup contained and validated 16 objects. A deliberately incomplete set was rejected. Restore targeted a separate run-owned PostgreSQL database/schema namespace and a separate blob root, leaving the source untouched during restore.

The restored host passed SDK complete reads, paginated listing, ranges, authorization denial, and reconciliation: 16 references, 16 physical blobs, no missing/unreferenced blobs, and no staging files. The original source dataset was then started and verified again. After successful evidence capture, only the run-owned source schema, restore database, roots, and backup sets were removed; summaries and logs were preserved.

## Verification and process workflow

- Package-pinned Release harness build: passed with zero warnings/errors.
- Production-shape 70-second rehearsal after final harness fixes: passed with 2,424 operations, 1,703 integrity checks, zero failures, 38.3 MiB sampled peak disk, and 16-object backup/restore.
- Definitive two-hour run: passed as reported above.
- Legacy duration-mode smoke regression: 5.050 seconds, 205 operations, 41 verified hashes, zero failures.
- Package-only consumer and broad library regressions were not repeated because no packed library code or candidate archive changed.

`soak/Launch.ps1`, `Status.ps1`, and `Stop.ps1` provide resumable launch/status/owned-stop behavior. Ownership validation uses PID, exact start time within Windows timestamp precision, and a run marker; no process is selected by port.

## Remaining gates

- Intended-server validation remains pending.
- The project license is still missing, so public distribution remains blocked.
- Shared-hosting S3 support remains unverified and is not claimed.
- Filesystem blobs remain single-instance; shared PostgreSQL does not provide multi-node blob storage.
- No package was published, source pushed/tagged, server provisioned, production deployed, or production data migrated.
