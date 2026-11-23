# Fifteenth Slice Completion Report

## Result

The fourteenth-slice CLI workflow was clarified, the exact validated deployment image received a focused persistent-SDK measurement, and the local direct-Kestrel TLS profile passed SDK/CLI compatibility with certificate validation enabled. The intended-server handoff is complete, but no target or access authorization was supplied, so deployment remains pending.

Shipping packages remain unchanged at `0.1.0-rc.2`, built from `4c5bbe953687023018eb3d2ca230f75e4e6f6c94`. Their SHA-256 values remain:

- `Audorix.ObjectStorage.0.1.0-rc.2.nupkg`: `F7A1847C1B9968BD1FF37CE27639D6ABD0256CAFFAF363372A128A3FDA4D08FE`
- `Audorix.ObjectStorage.Api.0.1.0-rc.2.nupkg`: `C283BDCD7CCDD75702B0D342781D366DB796AECC9A4F49BCBC6BD9D530510432`
- `Audorix.ObjectStorage.PostgreSql.0.1.0-rc.2.nupkg`: `0858DC2761023F421397B598C08C422B4C77CADB313462B471E0D9E173BA2CB4`
- `Audorix.ObjectStorage.S3.0.1.0-rc.2.nupkg`: `A88788A91C99EA99E9D1CAE882472D150B92A6C161642628B57CCD1ACF60D00E`

The validated image remains `sha256:ed51fa14ec1fdaec2aa615fa3313c4f986d27806b3b6059bc5ff4aeddfa178b3`. Its core, PostgreSQL, S3, and deployment-host assembly SHA-256 values remain `0e2c9fd08fc21350a8634c0276dce33edb17f9e822444bdfb37dcf6cb4448ef5`, `89cbdae568bb99c22b297b5e7627bee9c06b53d0f4439293df6144ecc5874b02`, `60245e8fe2a1c670f18caf8b0f0997f3a0652d32ff13839b0bfadb57862f6152`, and `68b65ad1542db9eb01daa57ef711318766c6bdf6bfb8c4e0471d2b398046ac47`. No image or package was rebuilt. The slice began from clean evidence-link commit `6bdcd73d2cbe2db83ebb968ad1120add72c6d1f8`; evidence-only harness, deployment-profile, and documentation work is not attributed to the rc.2 package source commit.

## Performance clarification

The reported 396.892 seconds covered 100 separate AWS CLI process launches and requests. It excluded provisioning, setup file generation, warm-up, backup/restore, lifecycle work, and cleanup; there was no deliberate pacing. Client startup and request/transfer time were not separately instrumented, so no per-request inference is valid.

The focused HTTP run used one persistent AWSSDK.S3 client, 20 warm-ups, concurrency 1, 200 samples for each 64 KiB small operation/range, 100 listing-page samples, and 30 verified 8 MiB transfer pairs. All 1,160 measured network requests completed with zero errors/retries and produced 1,620 latency timing samples. Representative p50/p95/p99 were: small PUT 10.22/15.07/23.30 ms, small GET end-to-end 3.75/6.09/11.23 ms, list page 4.63/6.68/12.02 ms, range 3.22/5.22/9.29 ms, 8 MiB PUT 72.82/151.97/169.21 ms, and 8 MiB GET end-to-end 81.69/160.79/246.34 ms. Measured transfer rates were 51.76 MiB/s upload and 58.17 MiB/s download. This is a bounded local concurrency-one comparison, not capacity or SLA evidence.

## Local TLS

An isolated TLS project used a trusted `CN=localhost` development certificate and direct Kestrel TLS on `https://localhost:5196/s3`. AWS CLI used the matching public CA bundle; the SDK used the Windows CurrentUser trust store. No certificate bypass was used. CLI and SDK passed upload, metadata/head, special-character keys, pagination/listing, full and range reads, hash verification, and delete. Invalid credentials and a 64 MiB-plus-one-byte upload were denied.

Every SDK request also carried `X-Forwarded-Host: attacker.invalid`; 280 measured network requests completed with zero errors/retries and produced 390 latency timing samples because no remote proxy was trusted. This verifies untrusted forwarded headers cannot alter the local direct-TLS SigV4 decision. It does not validate an intended reverse proxy.

The TLS Compose port merge, TLS-aware script URLs, internal health listener, and Compose-file selection were corrected. These are deployment-only changes; rc.2 assemblies and image contents did not change. The TLS project's containers, database, network, volumes, private-key export, and temporary secrets were removed after evidence capture. The prior HTTP deployment and original pilot were not replaced.

The HTTP harness deleted every test key and reported zero staging files. The storage engine intentionally retains newly unreferenced immutable blob files for its reclamation window. A broad cleanup was not forced against the preserved fourteenth-slice volume because it cannot distinguish this run's expired unreferenced blobs from older eligible data; doing so would violate the requirement to clean only resources created here. Normal owner-approved maintenance may reclaim them. No pre-existing container, volume, process, database, or pilot resource was removed.

## Verification and handoff

The modified harness builds in Release with zero warnings/errors. The TLS Compose profile resolves successfully, all JSON evidence parses, all PowerShell deployment scripts parse, and the preserved package files re-hash to the identities above. Final local tools were .NET SDK 10.0.303, Docker client/server 29.5.2, Docker Compose 5.1.3, AWS CLI 2.37.6, and AWSSDK.S3 package 4.0.103.4. The preserved deployment returned HTTP 200 at `http://127.0.0.1:5195/health/ready`; the original pilot returned HTTP 200 at storage `5185`, ReportStudio API `5165/api/health`, and frontend `5273`. Focused HTTP and TLS runs are machine-readable in `fifteenth-performance-http.json`, `fifteenth-tls-sdk-summary.json`, and `fifteenth-tls-validation-summary.json`. The server input template and exact preflight/deploy/validate/backup/rollback/cleanup flow are in `intended-server-handoff.md`.

## Repository

The clean baseline was `6bdcd73d2cbe2db83ebb968ad1120add72c6d1f8`. The complete implementation, machine evidence, and handoff were committed locally as `ff9d86e03afb9062c79dc419760f8f92270718ad` (`Complete fifteenth deployment validation slice`) using the repository's existing configured identity. This commit-link update is recorded in a separate local evidence commit to avoid claiming a commit contains its own hash. Final repository status was verified clean. No remote was created, and nothing was pushed or tagged.

Still required: explicit server identity and authorized access, OS/architecture, persistent filesystem details, PostgreSQL endpoint and separated identities, private endpoint or DNS, TLS termination and certificate provisioning, expected workload/resources, backup destination/retention/owner, monitoring destination/owner, and rollback digest/approver. Public DNS, public certificates, reverse proxies, Cloudflare, power loss, intended-server capacity, and multi-node operation remain unvalidated.

The next milestone is authorized intended-server deployment and validation. No further generic local hardening slice is proposed without a concrete finding.
