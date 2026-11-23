# Second-Slice Completion Report

## Implemented

- `net10.0` S3 adapter DLL hosted entirely through ASP.NET Core endpoint routing.
- PutObject, GetObject, HeadObject, and DeleteObject over path-style URLs.
- SigV4 Authorization-header authentication using method, host, raw path, query, and signed headers.
- Credential restrictions by exact bucket and operation flags.
- Signing-region and timestamp-window enforcement.
- Constant-time signature and payload-hash comparison.
- Streaming upload SHA-256 and optional Content-MD5 validation before the core engine commits metadata.
- Streaming downloads and metadata/Content-Type round trips.
- S3 XML errors and request identifiers.
- Explicit failures for unsupported methods, query operations, `aws-chunked`, checksum trailers, and additional checksum headers.
- Configurable endpoint path, demonstrated and tested at `/s3`.

## Executed Verification

On 2026-09-29, the local test suite executed against a real loopback Kestrel listener, not only an in-memory pipeline:

- 18 tests passed.
- 1 AWS CLI test was skipped and is not counted as passing evidence.
- AWSSDK.S3 4.0.103.4 completed PUT, GET, HEAD, and DELETE.
- Direct-to-S3 and S3-to-direct reads completed against one engine.
- Empty, Unicode, spaces, percent signs, and nested key paths round-tripped.
- An 8 MiB file exercised the streaming transfer path and round-tripped byte-for-byte by SHA-256. This does not establish bounded process memory for large files.
- Missing, wrong-secret, stale, and unauthorized requests were rejected.
- A tampered signed payload failed before commit and preserved the previous object.
- A cancelled HTTP overwrite preserved the previous committed object.
- Host disposal/restart preserved an S3-written object using the test metadata store.

The original nine tests used `JsonFileMetadataStore`, a test substitute. They did not use PostgreSQL. The real-listener tests also use that substitute unless the external PostgreSQL test is enabled.

The PostgreSQL group was subsequently executed against the configured `S3Storage` database through `PostgreSqlObjectStorageMetadataStore`. Four PostgreSQL tests passed:

- AWS SDK network upload and download.
- Successful overwrite, cancelled overwrite preservation, and concurrent same-key mutations.
- Host disposal/restart followed by a persisted network read.
- PostgreSQL 18 `pg_dump`/`pg_restore` recovery paired with a copied blob-root recovery set.

Each test used an isolated generated schema and removed only its own `audorix_s3_test_*` schema afterward.

## Unexecuted Checks

- AWS CLI: no callable `aws` executable was present. The externally gated CLI PUT/GET/DELETE test was skipped, not counted as passing.
- No commercial shared-hosting provider was available for deployment verification.

## Reuse Result

S3Server 7.3.2 and AWSSignatureGenerator 1.1.0 were inspected at the commits recorded in the architecture decision. Their listener/context coupling and body-buffering standard-signature path made direct reuse impractical for this bounded adapter. No upstream source was copied. S3Server and Watson were removed from the runtime dependency graph.

## Remaining Limitations

- `aws-chunked`, checksum trailers, and automatic SDK checksum modes are unsupported.
- Range and conditional requests, listing, bucket management, presigned URLs, multipart, and virtual-host addressing are unsupported.
- ETags are opaque version identifiers, not MD5 hashes.
- One process instance may own a storage root. PostgreSQL does not make local filesystem blobs multi-node storage.
- Normal process restart is tested; power-loss durability is not claimed.
- Local ASP.NET Core hosting is verified; provider-specific shared hosting is not.

## Recommended Next Slice

Build a PostgreSQL-backed S3 pilot in this order:

1. Execute PostgreSQL-backed overwrite, cancellation, concurrent mutation, host restart, and backup/restore tests.
2. Execute AWS CLI upload, download, and delete checks and record the exact required client settings and unsupported modes.
3. Measure process memory during substantially larger streamed uploads and downloads; do not infer bounded memory from the 8 MiB test.
4. Add and verify `ListObjectsV2` through the same PostgreSQL-backed network host.
5. Add `aws-chunked` and checksum support based on failures observed with the tested SDK and CLI versions.

The milestone exit criterion is a network-accessible PostgreSQL-backed pilot that reliably uploads, lists, downloads, overwrites, restarts, and deletes files. Shared-hosting deployment validation follows that foundation.
