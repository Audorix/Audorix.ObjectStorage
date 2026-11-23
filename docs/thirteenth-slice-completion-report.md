# Thirteenth Slice Completion Report

## Result

The exact `0.1.0-rc.2` candidate passed package-only AWS CLI compatibility, a clean two-hour mixed soak, bounded capacity/admission measurement, controlled failure recovery, restart, reconciliation, and paired PostgreSQL/blob backup and restore on the local single-machine environment. No shipping-library defect was demonstrated, so package contents and version were not changed.

This is controlled-pilot evidence only. It does not certify shared hosting, multi-node blobs, network filesystems, intended-server capacity, TLS/proxy deployment, actual disk exhaustion, power loss, or a production SLA.

## Candidate Identity

- Source commit: `4c5bbe953687023018eb3d2ca230f75e4e6f6c94`, clean at candidate build.
- Prior evidence commit: `011df5a58eff853d7e6cf105cba5e882e7ee3e58`.
- Thirteenth-slice harness and evidence commit: `407ac691d927978054981518ab4cad213940c690`.
- `Audorix.ObjectStorage.0.1.0-rc.2.nupkg`: `F7A1847C1B9968BD1FF37CE27639D6ABD0256CAFFAF363372A128A3FDA4D08FE`.
- `Audorix.ObjectStorage.Api.0.1.0-rc.2.nupkg`: `C283BDCD7CCDD75702B0D342781D366DB796AECC9A4F49BCBC6BD9D530510432`.
- `Audorix.ObjectStorage.PostgreSql.0.1.0-rc.2.nupkg`: `0858DC2761023F421397B598C08C422B4C77CADB313462B471E0D9E173BA2CB4`.
- `Audorix.ObjectStorage.S3.0.1.0-rc.2.nupkg`: `A88788A91C99EA99E9D1CAE882472D150B92A6C161642628B57CCD1ACF60D00E`.

All test hosts in this slice restored package references through the pinned candidate NuGet feed; they did not reference shipping source projects. Harness and documentation changes are evidence-only and are not attributed to the candidate source commit.

The test system was Windows 10.0.26200 x64, .NET runtime 10.0.12 / SDK 10.0.303, PostgreSQL server and tools 18.3, Intel i9-12900HK (14 cores/20 logical processors), and 63.7 GiB RAM. Client, Kestrel, filesystem, and PostgreSQL shared one machine.

## AWS CLI

AWS CLI `2.37.6` passed PUT, HEAD, metadata, hash-matched GET, ListObjectsV2, range GET, and DELETE through the rc.2 endpoint. The key `metadata/space +percent%25/question?literal.txt` and metadata `owner=audorix,purpose=rc2` exercised signed headers and reserved characters.

Supported CLI operation required `AWS_REQUEST_CHECKSUM_CALCULATION=when_required`, `AWS_RESPONSE_CHECKSUM_VALIDATION=when_required`, path-style addressing, and region `us-east-1`. With default checksum behavior restored, PUT failed explicitly with exit code 254 and S3 `NotImplemented` (`Additional S3 checksum headers are not supported in this slice`); HEAD confirmed no object was created. Commands in [thirteenth-aws-cli-summary.json](thirteenth-aws-cli-summary.json) redact secrets, paths, and the ephemeral endpoint.

## Capacity and Admission

The clean final capacity run used 15-second small-object stages at concurrency 1/2/4/8/16, 10/100/1,000-object listings, 16/64 MiB transfers at concurrency 1/2/4, and 16 offered overload clients. All normal attempts completed with zero failures/timeouts/retries. Results were non-monotonic on the shared local machine; no maximum capacity claim is made.

The explicit 4-active/2-queued overload phase completed six requests, rejected ten with expected `SlowDown`, recorded zero unexpected failures, returned staging to zero, and passed a recovery PUT/GET/DELETE. The conservative tested-local starting envelope is client concurrency 4, server 8 active plus 4 queued, 64 MiB objects, listing page size 100, and a 2 GiB reserve. See [capacity-and-operating-limits.md](capacity-and-operating-limits.md) and [thirteenth-capacity-summary.json](thirteenth-capacity-summary.json).

An earlier capacity attempt left a run-owned listing process after the command session was cut off. It overlapped the first soak attempt, which recorded four internal errors. Both were stopped by exact PID/path/run-marker ownership, and neither dataset is used. The final capacity run and final soak were executed separately with no competing validation harness.

## Two-Hour Soak and Backup

Run `20260930210955-050d2f23` used the exact rc.2 hashes, unique schema/bucket/credentials/roots, four workers, 64 KiB normal objects, periodic 4 MiB objects, a 1 GiB run budget, 32 active plus 8 queued host admission, and 60-second durable checkpoints.

- Actual workload: `02:00:00.0036454`.
- Completed operations: 204,220; integrity checks: 132,367.
- Uploaded: 11,776,098,304 bytes; downloaded: 17,825,298,214 bytes.
- Expected controlled cancellations: 2,167; expected authorization denials: 2.
- Unexpected failures: 0; retries: 0.
- Peak sampled managed heap: 26,739,248 bytes; working set: 121,896,960 bytes; root: 54,463,754 bytes; staging files: 4 during active requests; PostgreSQL connections: 1.
- Upload p50/p95/p99: 311/1,345/2,224 ms; full read: 3/177/373 ms; overwrite: 321/1,413/2,254 ms; listing: 13/226/437 ms.
- Drain reconciliation: zero missing blobs, zero unreferenced blobs, zero staging files.

The same controller restarted the source host and reverified retained objects and authorization. It quiesced the source, created and validated a 16-object paired backup, rejected an inconsistent backup set, restored to a separate database/root, verified listing/full reads/ranges and zero reconciliation differences, then re-opened and reverified the original source. All owned schemas, restore database, roots, and backup directories were cleaned. [thirteenth-soak-summary.json](thirteenth-soak-summary.json) is the complete machine record.

## Failure Recovery

The package-only recovery harness used a run-owned PostgreSQL TCP proxy. During proxy interruption, upload and listing failed; after reconnect, the acknowledged baseline and a new acknowledged object verified correctly. PostgreSQL itself was never stopped.

The disk-write fault replaced only the disposable run staging directory with a regular file. PUT failed explicitly, the previous object remained intact, and the directory was restored. This was injected I/O failure, not actual disk-full testing.

At a reproducible non-empty staging-file barrier, abrupt host termination produced no acknowledgment and restart exposed the previous complete version. A normally acknowledged overwrite survived immediate termination and restart with matching bytes. At a new-blob/publication barrier, the request was unacknowledged and restart exposed the previous complete version, never partial bytes. Subsequent list, full read, range, overwrite, delete, cleanup, and reconciliation passed; live data survived cleanup and missing/orphan/staging counts were zero. Process termination is not power-loss certification. See [thirteenth-recovery-summary.json](thirteenth-recovery-summary.json).

## Verification and Pilot

- Isolated-output Release solution build: passed, zero warnings/errors. The normal sample-host output was locked by an unrelated existing port-5085 process and was not stopped.
- Full source regression suite with external PostgreSQL configuration and AWS CLI on PATH: 40 passed, 0 failed, 0 skipped.
- Exact-candidate package-only consumer: 1 passed, 0 failed, 0 skipped.
- Existing pilot status remained owned and ready at storage `http://127.0.0.1:5185/`, API `http://127.0.0.1:5165/api/health`, and frontend `http://127.0.0.1:5273/`.

The pilot's loaded Audorix DLL hashes were observable but did not match the rc.2 package DLL hashes. Its version is therefore recorded as unknown and none of its continuity/browser evidence is attributed to rc.2. The pilot was not rebuilt, stopped, attacked, or restarted.

## Reproducibility

- Capacity: `capacity/Run.ps1`; details in `capacity/README.md`.
- Long soak: `soak/Launch.ps1`, `soak/Status.ps1`, and ownership-safe `soak/Stop.ps1`.
- Recovery: `recovery/Run.ps1`; details in `recovery/README.md`.
- AWS CLI: `validation/Run-AwsCliRc2.ps1`.

Secrets remain external or in ignored local configuration. Runtime roots, package caches, logs, process records, and generated credentials are ignored. No shared PostgreSQL service was interrupted, no real volume was exhausted, and cleanup targeted only run-owned resources.

## Remaining Gates

- The project license is still missing; public distribution remains blocked. No license was chosen.
- TLS/reverse-proxy path preservation, filesystem identity/permissions, PostgreSQL roles/timeouts, Data Protection, and recycle behavior require validation on the intended deployment.
- Actual disk-full, machine power loss, shared hosting, network filesystems, and multi-node blob access remain untested and unsupported claims.
- Intended-server capacity requires a separate load generator, realistic network/TLS, longer stages, and deployment-specific storage/PostgreSQL telemetry.

No package was published, no source was pushed or tagged, no production data was migrated, and no external deployment was performed.
