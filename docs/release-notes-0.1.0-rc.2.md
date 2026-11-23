# Audorix.ObjectStorage 0.1.0-rc.2

Security-focused prerelease candidate. No S3 operations were added.

- Requires all semantic `x-amz-*` headers to participate in SigV4 `SignedHeaders`.
- Adds expiring, credential-bound S3 continuation tokens and expiring application API tokens.
- Adds startup-validated key, metadata, header, query, token, concurrency, and queue limits while preserving streaming.
- Rejects unsafe content type and metadata header values before writing bytes.
- Removes unauthenticated direct read/write routes from the sample host and gives it an explicit bounded profile.

The public-distribution license remains unresolved. Do not publish this candidate. The rc.1 two-hour soak does not apply to these changed binaries; run a fresh long soak before promotion.
