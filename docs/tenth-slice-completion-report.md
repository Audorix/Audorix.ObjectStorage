# Tenth Slice Completion Report

Date: 2026-09-30

This report covers only the tenth bounded slice. The previously completed ninth-slice packaging work is background, not evidence for this slice.

## Source traceability

- No parent Git repository applied to `Audorix.ObjectStorage`, so a repository was initialized at that directory only.
- Baseline source commit: `9bbf4e4203cdb387d2ce66f966f6410f62b145f4` (`Establish Audorix.ObjectStorage release baseline`).
- The commit used the already-configured Git identity. No identity, remote, tag, or push was created.
- The staged review contained 86 source/documentation files and no forbidden runtime or secret paths. The review printed file names and configuration key names, not values.
- `database.json`, credentials, local runtime overrides, `.pilot`, blobs, staging, backups, logs, test output, build output, and package output are ignored.
- `Phase0Work`/ReportStudio is a sibling directory and was not staged in this repository.
- The baseline was clean before candidate packaging. Evidence documentation is recorded separately so it is not represented as candidate input.

## Candidate release evidence

Fresh candidates were built into `artifacts/candidates/9bbf4e4203cd/packages`, separate from the preserved ninth-slice artifacts. They were built from the clean baseline commit above. The package version remains `0.1.0-rc.1` because this slice changed host/pilot/test tooling and documentation, not the packed library contents.

| Package | Bytes | SHA-256 |
| --- | ---: | --- |
| `Audorix.ObjectStorage.0.1.0-rc.1.nupkg` | 27,544 | `BA71891122A6C2D98534DB3216DB1F931019794915A8181C4D056D939D9FAA16` |
| `Audorix.ObjectStorage.Api.0.1.0-rc.1.nupkg` | 25,435 | `605C419239B5286611B5FF3588E23C8FB80672A666A00B91568D84577079A0CD` |
| `Audorix.ObjectStorage.PostgreSql.0.1.0-rc.1.nupkg` | 40,429 | `8F8D9458B88C29B18155BF9797253366CA5E974F1DEB597AF6CCCF5C4C086545` |
| `Audorix.ObjectStorage.S3.0.1.0-rc.1.nupkg` | 32,129 | `F0003B44DC6A86B1033B25D5C59983AE208A8AD5BABFD8CC582887CD6B1AE73E` |

The package-content validator passed all four candidates. A restore using an isolated package cache and a NuGet configuration pointing at this candidate directory was followed by the package-only consumer test: 1 passed, 0 failed, 0 skipped. The complete machine-readable record is `artifacts/candidates/9bbf4e4203cd/release-manifest.json`.

These statements apply only to the fresh candidates. Existing packages are not claimed to have originated from the new commit.

## Reproducible pilot

The pilot uses `pilot/Provision.ps1`, `pilot/Start.ps1`, `pilot/Status.ps1`, and `pilot/Stop.ps1`. Non-secret defaults are in `pilot/templates/pilot.settings.template.json`; the secret template contains placeholders only. Provisioning writes ignored persistent state under `.pilot`, preserves existing files, accepts PostgreSQL through `AUDORIX_PILOT_POSTGRES` or the ignored local provider file, and uses the isolated `audorix_pilot_tenth` schema and `reportstudio-pilot-tenth` bucket by default.

Start creates private Release build outputs and process records. Each record contains the PID, exact process start time, and command marker. Status and stop validate all three. Stop works from those records in reverse order; it neither selects nor kills by port. Unmanaged processes already using ports 5085 and 5173 were left untouched.

Readiness endpoints:

- Storage: `http://127.0.0.1:5185/`
- ReportStudio API: `http://127.0.0.1:5165/api/health`
- Frontend: `http://127.0.0.1:5273/`

Development seed identities remain conditional on Development. Testing authentication remains conditional on Testing; neither is available in Production.

## Restart and browser evidence

The pilot was started, checked ready, stopped through its ownership records, and started again. After restart, file metadata and bytes remained available through PostgreSQL metadata plus the persistent filesystem root, and authorization still applied.

A real browser was used against the frontend:

- Development Owner saw the BoostShop workspace, opened metadata for `Pilot invoice.pdf`, and rendered the PDF preview visibly.
- `Pilot colors.bmp` rendered visibly as an image preview.
- Downloads/previews returned the deterministic seeded bytes. The PDF was 598 bytes with SHA-256 `A23D6E83717098334B09A7EBDBC545EAD757978D86FCE4FBD8B91A2562D4BF20`; the BMP was 46,134 bytes with SHA-256 `67B911F5F7D2B25BE4DDB8AC1C932829329D7D78979DDB77BDF3CCC7A2BBFF7D`.
- Development Reviewer saw only Northwind Pilot and `Private brief.pdf`. A direct reviewer request for the owner's workspace returned HTTP 404.

The first browser pass exposed persisted seed metadata whose bytes were absent from the newly isolated root. Development seeding was corrected to restore missing deterministic seed bytes and refresh only the seed version ETag when required. No database reset or production configuration overwrite was performed.

## Sustained validation

The existing load harness now supports `--duration-seconds`, `--concurrency`, `--object-kib`, and `--output`. Each worker repeatedly performs upload, full read with SHA-256 verification, ListObjectsV2 verification, byte-range verification, and delete against an isolated schema, root, and bucket.

The short run requested 20 seconds at concurrency 4 with 64 KiB objects. Actual duration was 20.0297278 seconds. It completed 13,515 operations, including 2,703 operations of each type and 2,703 verified full-object hashes, with 0 failures. Aggregate p95 latency by operation was: upload 35.6098 ms, read 10.6322 ms, list 7.1975 ms, range 5.8306 ms, delete 5.1288 ms. Full results are in `docs/tenth-sustained-results.json`.

`docs/sustained-testing.md` documents both the short command and a six-hour command for the intended server. No six-hour run or server deployment is claimed.

## Build and regression results

- ObjectStorage Release regression run: 35 passed, 0 failed, 1 skipped because AWS CLI was not resolvable in that process context.
- Targeted AWS CLI compatibility rerun with AWS CLI 2.37.6: 1 passed, 0 failed, 0 skipped.
- ReportStudio API Release tests: 14 passed, 0 failed, 0 skipped.
- Candidate package-only consumer: 1 passed, 0 failed, 0 skipped.
- Load harness Release build and the three pilot component Release builds succeeded with no errors.

The combined AWS result is reported as the main run plus a targeted successful rerun, not as a single uninterrupted 36-test run.

## Remaining blockers and limits

- The owner has not supplied a project license. Public package distribution remains blocked; no license was chosen or invented.
- Shared-hosting S3 support remains unverified and is not claimed.
- Filesystem blobs remain single-instance. A shared PostgreSQL database does not provide multi-node blob storage.
- The short sustained run is functional validation, not a long-duration soak or production capacity result.
- No package was published, no source was pushed or tagged, no production deployment occurred, and no production data was migrated.
