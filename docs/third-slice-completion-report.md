# Third Slice Completion Report

## Implemented

- Authenticated path-style `ListObjectsV2` in the existing ASP.NET Core endpoint.
- Prefix, delimiter/CommonPrefixes, MaxKeys, continuation token, StartAfter, and `encoding-type=url`.
- A distinct `ListBucket` credential permission checked before metadata access.
- Database-neutral listing models and engine contract; no database type appears in the public storage API.
- Bounded PostgreSQL pagination using UTF-8 byte ordering independent of locale collation and literal SQL wildcard matching.
- Versioned HMAC-SHA256 continuation tokens scoped to the bucket and listing parameters.

Object bytes remain on the filesystem. PostgreSQL stores metadata only. A shared PostgreSQL database does not provide shared or multi-node blob storage.

## Client Evidence

Executed against a real loopback Kestrel listener, the production PostgreSQL provider, isolated PostgreSQL schemas, isolated buckets, and isolated filesystem roots:

- AWSSDK.S3 4.0.103.4: PutObject, GetObject, HeadObject, DeleteObject, and ListObjectsV2 passed.
- AWS CLI 2.37.6 (`aws-cli/2.37.6 Python/3.14.6 Windows/11 exe/AMD64`): configured `s3api` put-object, get-object, head-object, delete-object, and list-objects-v2 passed.
- AWS CLI high-level `s3 ls s3://integration --recursive` passed without requiring another bucket API.
- Byte equality, content type, custom metadata, empty files, nested keys, spaces, percent signs, ampersands, and XML-sensitive characters were covered.

The AWS CLI test used process-only credentials plus isolated config and credential paths. It did not read or modify global AWS credentials.

## Executed Verification

- .NET SDK 10.0.303 Release build: succeeded with zero warnings and zero errors.
- Full Release test suite: 22 passed, 0 failed, 0 skipped.
- Eight tests used the configured local `S3Storage` database through `PostgreSqlObjectStorageMetadataStore`; none of those eight used the JSON substitute.
- PostgreSQL resources used unique test schemas and filesystem roots and were removed after each test.

## Default Versus Compatibility Profile

AWS CLI default upload behavior was tested first. Version 2.37.6 emitted its supported-mode checksum request and the bounded endpoint returned `501 NotImplemented`. No server-side integrity or signature validation was disabled.

The passing compatibility profile was:

```text
AWS_REQUEST_CHECKSUM_CALCULATION=WHEN_REQUIRED
AWS_RESPONSE_CHECKSUM_VALIDATION=WHEN_REQUIRED
```

The same checksum posture is used by the AWSSDK.S3 tests, together with path-style addressing and `UseChunkEncoding = false` for PUT.

## Listing Evidence

Real PostgreSQL/HTTP tests cover empty and populated results, default and zero MaxKeys, prefix filtering, nested delimiter grouping, mixed object/CommonPrefix page boundaries, multi-page traversal without duplicates or omissions on an unchanged dataset, StartAfter, Unicode and XML-sensitive keys, literal `%` and `_`, URL encoding, malformed/tampered/cross-bucket tokens, and GET-without-LIST authorization.

Continuation is forward-only, not snapshot-isolated. The concurrent-mutation test confirms that inserts after the cursor may appear, inserts before it are not revisited, and deleted entries are absent.

## Remaining Limits

- `aws-chunked`, checksum trailers, additional checksum headers, multipart uploads, ranges, conditional requests, presigned URLs, virtual-host addressing, and bucket management are not implemented.
- The 8 MiB streaming test is not evidence of bounded memory for very large objects. The planned 1 GiB upload/download memory measurement remains pending.
- The inconsistent database/blob backup-set failure demonstration remains pending.
- One process may own a blob root. Shared PostgreSQL metadata does not make independent local disks coherent.
- Shared-hosting deployment has not been verified and is not claimed.

## Recommended Next Slice

Run the predefined 1 GiB upload/download memory measurement and the inconsistent recovery-set test. Then use the captured AWS CLI default failure to add narrowly scoped checksum and `aws-chunked` support, without expanding into multipart or shared-hosting deployment.
