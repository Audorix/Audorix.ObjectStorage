# PostgreSQL-Backed S3 Pilot

## Objective

Establish production-oriented persistence and network interoperability before expanding the S3 protocol surface or making shared-hosting claims.

The pilot must use:

- A real PostgreSQL instance configured through `AUDORIX_OBJECTSTORAGE_POSTGRES`.
- The production `PostgreSqlObjectStorageMetadataStore`.
- Filesystem object bytes in a persistent test directory.
- A real ASP.NET Core Kestrel listener.
- The AWS SDK for .NET and AWS CLI as external clients.

Docker may provision PostgreSQL for testing, but it remains test infrastructure and is not a library or deployment requirement.

## Required Persistence Tests

1. Upload, read, overwrite, and delete through S3 with metadata and byte-for-byte verification.
2. Cancel and fail overwrites at multiple transfer points; the previous committed object must remain readable.
3. Run concurrent mutations against the same and different keys and verify one complete committed result per key.
4. Dispose and restart the ASP.NET Core host and storage engine, then read previously committed objects.
5. Back up PostgreSQL metadata and the blob root as one recovery set, restore both into a clean environment, and verify all object references and bytes.
6. Demonstrate and document expected failure when a restored database and blob directory are from inconsistent points in time.

## Required Client Tests

- AWS SDK for .NET: PutObject, ListObjectsV2, GetObject, HeadObject, and DeleteObject.
- AWS CLI: upload, list, download, head, and delete against the same host.
- Direct API upload followed by SDK and CLI reads.
- SDK and CLI uploads followed by direct API reads.
- Exact keys containing Unicode, spaces, percent signs, `#`, `?`, and nested prefixes.
- Record client versions, endpoint URL, signing region, path-style setting, checksum settings, and every unsupported mode observed.

## Large-Transfer Memory Test

Use files substantially larger than the expected buffering threshold, with an initial minimum of 1 GiB for both upload and download. Generate and verify content as streams so the test harness does not retain the complete object in memory.

Record at least:

- Baseline process working set and managed heap after warm-up.
- Peak process working set and managed heap during upload.
- Peak process working set and managed heap during download.
- File size, elapsed time, configured copy buffer, and number of concurrent transfers.
- Garbage collections and whether server or client retries occurred.

The result must show that memory growth is governed by configured buffers and concurrency rather than object size. Define the numerical acceptance threshold before running the final measurement and retain the raw measurements with the completion report.

## ListObjectsV2 Scope

Implement only the fields required for reliable enumeration in this milestone:

- Bucket, prefix, delimiter, `max-keys`, continuation token, StartAfter, key count, truncation state, object key, size, last-modified time, ETag, and URL encoding are implemented.
- PostgreSQL uses explicit `C` collation for S3-compatible UTF-8 byte ordering and escaped LIKE patterns for literal `%` and `_` characters.
- Queries fetch at most MaxKeys plus one deduplicated object/CommonPrefix entry; the adapter retains only that page.
- Tokens are versioned, HMAC-SHA256 protected, and scoped to bucket and listing parameters.
- Pagination is a strict forward cursor, not a snapshot. Inserts after the cursor can appear; inserts at or before it do not; deletes disappear.

## Exit Criteria

- All PostgreSQL lifecycle tests pass without using the JSON substitute.
- AWS SDK and AWS CLI upload, list, download, and delete pass over a real listener.
- Large-transfer measurements satisfy the predefined memory threshold.
- Backup/restore produces readable metadata and bytes from a consistent recovery set.
- No multi-node claim is made: a shared database still does not make local blob storage shared.
- Remaining checksum and `aws-chunked` gaps are captured from actual client failures and prioritized from that evidence.

## Current Status

Completed against the configured local `S3Storage` PostgreSQL database:

- Production-provider SDK upload/download round trip.
- Overwrite and cancelled-overwrite preservation.
- Concurrent same-key mutations with a complete committed result.
- Host restart and persisted read.
- Consistent PostgreSQL `pg_dump`/`pg_restore` plus blob-root backup/restore.
- AWSSDK.S3 4.0.103.4 ListObjectsV2 through Kestrel, including prefixes, delimiter grouping, multiple pages, special keys, authorization, and invalid tokens.
- AWS CLI 2.37.6 `s3api` PUT/GET/HEAD/DELETE/ListObjectsV2 and `aws s3 ls --recursive` through Kestrel.
- AWS CLI default upload failure captured as `501 NotImplemented`; the verified profile sets request and response checksum modes to `WHEN_REQUIRED`.

Still required for milestone exit:

- None for the bounded local pilot. The 1 GiB/concurrency measurements and validated recovery-set tests completed in the fourth slice.

The next milestone is integration as an Audorix S3 storage provider in one application. This does not change the single-instance filesystem restriction or establish shared-hosting support.
