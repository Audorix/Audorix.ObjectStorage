# Twelfth Slice Completion Report

Date: 2026-09-30

This report covers the twelfth security-review slice. It does not reuse ninth-slice packaging or the eleventh rc.1 two-hour soak as evidence for the changed rc.2 binaries.

## Result

The implementation review found one high, three medium, one low, and two informational issues. The high/medium/low code findings were fixed and have regression evidence. The informational findings are documented constraints: SigV4 replay inside clock skew and protection against local path substitution by directory ownership rather than key normalization.

No unresolved implementation finding blocks an isolated controlled local pilot with the documented hardening profile. Promotion beyond that remains blocked by the missing license, a fresh long soak for rc.2, an rc.2 AWS CLI rerun where the CLI is available, and environment-specific proxy/TLS/permissions validation. Shared-hosting and multi-node support are not claimed.

## Source and Candidate Identity

- Reviewed and packaged source commit: `4c5bbe953687023018eb3d2ca230f75e4e6f6c94` (`Harden storage security boundaries for rc.2`).
- Package version: `0.1.0-rc.2`, incremented because shipping library contents changed.
- The repository was clean at candidate build. Later changes update only package-pinned validation tooling and evidence.
- Candidate directory: `artifacts/candidates/4c5bbe9`; rc.1 artifacts were preserved.
- Manifest: `artifacts/candidates/4c5bbe9/release-manifest.json`.
- No remote, push, tag, publication, external deployment, or production data change occurred.

| Package | Bytes | SHA-256 |
| --- | ---: | --- |
| `Audorix.ObjectStorage.0.1.0-rc.2.nupkg` | 29,055 | `F7A1847C1B9968BD1FF37CE27639D6ABD0256CAFFAF363372A128A3FDA4D08FE` |
| `Audorix.ObjectStorage.Api.0.1.0-rc.2.nupkg` | 28,473 | `C283BDCD7CCDD75702B0D342781D366DB796AECC9A4F49BCBC6BD9D530510432` |
| `Audorix.ObjectStorage.PostgreSql.0.1.0-rc.2.nupkg` | 40,763 | `0858DC2761023F421397B598C08C422B4C77CADB313462B471E0D9E173BA2CB4` |
| `Audorix.ObjectStorage.S3.0.1.0-rc.2.nupkg` | 34,804 | `A88788A91C99EA99E9D1CAE882472D150B92A6C161642628B57CCD1ACF60D00E` |

Package inspection passed for identity, dependencies, net10.0 DLL/XML, README/notices, forbidden paths, and known secret/local-configuration markers. No package declares a license because none was supplied; that remains a publication blocker.

## Findings and Fixes

| ID | Severity | Finding | Disposition |
| --- | --- | --- | --- |
| SEC-001 | High | Sample host exposed unauthenticated direct bucket/read/write routes. | Fixed by removing routes. |
| SEC-002 | Medium | Unsigned `x-amz-meta-*` could affect a signed PUT. | Fixed by requiring every semantic `x-amz-*` header in `SignedHeaders`. |
| SEC-003 | Medium | Pagination tokens did not expire; S3 tokens were credential-transferable. | Fixed with expiry, S3 access-key binding, and authorization-before-decode. |
| SEC-004 | Medium | Header/query/key/metadata/token and expensive-operation admission were incomplete. | Fixed with startup-validated limits, fixed concurrency, and bounded queues. |
| SEC-005 | Low | Direct callers could store response-header-unsafe metadata/content type. | Fixed with bounded pre-write validation. |
| SEC-006 | Informational | SigV4 requests replay within accepted clock skew. | Accepted and documented; timestamp is not one-time protection. |
| SEC-007 | Informational | Privileged local reparse-point substitution is outside the implementation boundary. | Documented host ownership and directory-permission requirement. |

Full threat boundaries, exploit conditions, evidence, and residual risks are in [security-review.md](security-review.md). Deployment controls are in [security-hardening-guide.md](security-hardening-guide.md). Machine-readable results are in [security-test-summary.json](security-test-summary.json).

## Resource Controls

The sample profile sets 512 MiB maximum upload, 1024 UTF-8-byte keys, 8 KiB query, 100 headers/32 KiB aggregate header bytes, 8 KiB tokens, 15-minute tokens, 32 active operations, and 8 queued operations. Core defaults cap metadata at 50 entries/8 KiB and content type at 256 characters. List pages remain at most 1000.

S3 overload returns `503 SlowDown`; API overload returns `503 service_unavailable` and `Retry-After: 1`. Oversized upload returns `413 EntityTooLarge`; invalid bounds return protocol-safe `400` responses. A real-Kestrel cancellation/overload test observed immediate excess-work rejection, subsequent permit recovery, and no staging files. Hash mismatch and cancellation preserved the prior committed object.

These limits complement, not replace, Kestrel/proxy request-header timeout, data-rate, connection/stream, and idle limits. Slow downloads retain admission until completion or cancellation.

## Verification

- Isolated full Release build: passed, 0 warnings and 0 errors. A normal host output was locked by the existing pilot, so the successful build used a separate temporary output and did not stop the pilot.
- ObjectStorage regressions: 39 passed, 0 failed, 1 skipped. Tests used real Kestrel; PostgreSQL cases used unique schemas and roots.
- AWS SDK behavior: passed in the main regressions and package-only consumer for supported PUT/list/get/head/range/delete behavior, special keys, auth denial, restart, and API paths.
- AWS CLI rc.2: skipped because `aws` was unavailable on PATH. It is not reported as passed.
- Package-only rc.2 consumer: 1 passed, 0 failed, 0 skipped after restore from the isolated candidate feed.
- ReportStudio production boundary: 3 passed, 0 failed, 0 skipped. Production used cookie auth, rejected the testing header, and did not map development sign-in.
- NuGet advisory checks on 2026-09-30: no vulnerable packages reported for the shipping solution or relevant ReportStudio integration from the configured sources. Tool: .NET SDK 10.0.303 `dotnet list package --vulnerable --include-transitive`.
- Package archive security inspection: passed all four. Credentials, `database.json`, runtime config, connection strings, blobs, logs, and build paths were absent.

The bounded rc.2 mixed run lasted 60.0274297 seconds at concurrency 4 with 64 KiB objects. It completed 52,555 operations: 10,511 each of upload, full read, list, range, and delete; 10,511 hashes were verified; failures were zero. Upload p95 was 35.0085 ms, read 6.2133 ms, list 5.4689 ms, range 3.98 ms, and delete 3.4 ms. See [twelfth-sustained-results.json](twelfth-sustained-results.json).

The eleventh two-hour result belongs only to rc.1. Another full long soak is required for rc.2.

## Pilot and Deployment State

The pre-existing pilot was not attacked, rebuilt, stopped, or restarted. Its ownership-aware status check remained ready at storage `http://127.0.0.1:5185/`, ReportStudio `http://127.0.0.1:5165/api/health`, and frontend `http://127.0.0.1:5273/`. This is continuity evidence for the existing pilot, not proof that it is running rc.2.

Forwarded headers are not enabled by the library. Hosts that enable them must trust only configured proxies/networks and preserve the signed host/path. TLS, `AllowedHosts`, CORS/CSRF, logging redaction, Data Protection key persistence, filesystem ownership, PostgreSQL least privilege, pool/timeouts, and backup encryption/authenticity remain deployment responsibilities.

## Remaining Gates

- The project license is missing; public distribution is blocked. No license was selected or invented.
- Run a fresh long soak for the exact rc.2 package hashes.
- Run AWS CLI compatibility against rc.2 on a host where the CLI is available.
- Validate TLS/proxy path preservation, permissions, secrets, Data Protection, and PostgreSQL roles on the intended server.
- Shared hosting, network filesystems, multi-node blobs, abrupt crash, and power-loss behavior remain unverified and are not claimed.
