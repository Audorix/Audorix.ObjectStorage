# Fourth Slice Completion Report

## Implemented

- A separate-process large-transfer harness using real Kestrel, PostgreSQL, filesystem blobs, and AWSSDK.S3 4.0.103.4.
- Independent server and client managed-heap/working-set sampling at 100 ms intervals after warm-up.
- Incremental generation, SHA-256 verification, and download length validation without whole-object buffers.
- Configurable transfer sizes and concurrency, with cancelled-overwrite and staging-cleanup checks.
- An explicit S3 endpoint object-size option. The default removes Kestrel's 30,000,000-byte endpoint cap; deployments may configure a positive limit.
- Engine maintenance that rejects new mutations, drains admitted mutations, and prevents cleanup during backup.
- Versioned backup manifests, PostgreSQL dump and inventory checksums, streamed ordered NDJSON inventories, per-blob lengths/SHA-256, and atomic publication.
- Pre-activation validation and isolated restore with database-reference reconciliation.

## Predefined Memory Budget

Before the final run, the harness budget was:

- Server managed-heap growth: 128 MiB plus 16 MiB per concurrent transfer.
- Server working-set growth: 512 MiB plus 64 MiB per concurrent transfer.
- Copy buffer: 128 KiB.
- Client retries: zero.

The budget is a practical regression threshold for this harness, not a supported object-size or concurrency capacity claim.

## Measured Results

Environment: .NET 10.0.12, Microsoft Windows 10.0.26200, Release build. Each throughput figure is aggregate for the stated concurrency.

| Direction | Size | Concurrency | Duration | Throughput | Server heap growth | Server working-set growth |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Upload | 64 MiB | 1 | 0.283 s | 225.99 MiB/s | 0.92 MiB | 0.47 MiB |
| Download | 64 MiB | 1 | 0.124 s | 515.54 MiB/s | 0.39 MiB | 0.02 MiB |
| Upload | 64 MiB | 4 | 0.969 s | 264.27 MiB/s | 5.39 MiB | 4.53 MiB |
| Download | 64 MiB | 4 | 0.389 s | 658.90 MiB/s | 1.36 MiB | 0.42 MiB |
| Upload | 1 GiB | 1 | 7.429 s | 137.83 MiB/s | 12.70 MiB | 15.91 MiB |
| Download | 1 GiB | 1 | 3.376 s | 303.35 MiB/s | 3.46 MiB | 0.29 MiB |
| Upload | 1 GiB | 4 | 13.546 s | 302.38 MiB/s | 13.75 MiB | 5.56 MiB |
| Download | 1 GiB | 4 | 3.265 s | 1254.66 MiB/s | 11.46 MiB | 6.18 MiB |

All measured phases stayed below the predefined budget. Client growth was also sampled separately; its maximum managed-heap growth was 7.35 MiB and maximum working-set growth was 8.81 MiB. Hashes and lengths matched. The large cancelled overwrite preserved the previous bytes, and zero staging files remained. The raw artifact is `docs/large-transfer-results.json`.

Working-set movement includes runtime, file-cache, HTTP, and sampling effects and is not proof by itself. The comparison across 64 MiB and 1 GiB shows no growth proportional to object size in this run, but results are machine-specific and should be repeated on the deployment host.

## Backup and Restore Evidence

Real PostgreSQL tests cover successful manifest backup, validation, clean-schema/root restore, object bytes, content type and custom metadata, missing/corrupted blobs, truncated and mixed dumps, mixed blobs, unsupported manifest version, unsafe paths, duplicate inventory entries, incomplete interrupted sets, maintenance mutation rejection, resumed mutation after maintenance, and failed restore leaving active data unchanged.

The manifest is a corruption detector, not an authenticated signature. Restore requires an absent schema and absent destination root and never activates a candidate automatically.

## Executed Verification

- .NET SDK 10.0.303 Release solution build: succeeded with zero warnings and zero errors.
- Full Release suite: 25 passed, 0 failed, 0 skipped.
- Eleven tests used the configured local `S3Storage` database through the production PostgreSQL provider.
- The explicit Release load harness completed all eight measured upload/download phases plus cancellation and staging checks.

## Remaining Limits

- External database/filesystem writers must be stopped during backup; only mutations through the engine are coordinated.
- Reverse proxies and hosting platforms may impose request limits independent of the endpoint option.
- Measurements cover one local Windows machine and concurrency 1 and 4, not production capacity.
- S3 protocol limitations from the third slice remain unchanged.
- Shared-hosting and multi-node blob storage remain unverified and unsupported.

## Recommended Next Slice

Integrate Audorix.ObjectStorage as the S3 storage provider in one Audorix application, keeping one application instance and one persistent blob root while validating application workflows, configuration, startup, maintenance, and recovery.
