# Implementation Security Review

Review date: 2026-09-30

This is an implementation security review of the supported Audorix.ObjectStorage core, PostgreSQL provider, S3 endpoint, authenticated application API, backup tooling, sample host, and ReportStudio integration. It is not an independent penetration test, certification, or statement about an unreviewed deployment.

## Scope and Trust Boundaries

Assets are object bytes, names and metadata, PostgreSQL metadata, S3 credentials, application identities and grants, Data Protection keys, continuation tokens, backups, and availability. Entry points are `IObjectStorageEngine`, `/s3`, the authenticated API, PostgreSQL, the storage root, and backup/restore commands.

- The embedding host owns trusted configuration, authentication, authorization, TLS, proxy and CORS/CSRF policy, logging, process identity, persistent directories, and lifecycle.
- HTTP clients are untrusted and may have no credential, a narrow valid credential, or a compromised credential. They may send malformed, replayed, slow, concurrent, or tampered requests.
- PostgreSQL and filesystem administration are trusted infrastructure. An attacker able to change the database, root, process memory, Data Protection keys, or backup set is outside the HTTP attacker model.
- Keys are logical identifiers. Generated blob IDs, never object keys, select filesystem paths. PostgreSQL values are parameters.

The supported topology is one active engine for one local filesystem root, with PostgreSQL metadata. A shared database does not make filesystem blobs multi-node. Shared hosting, network filesystems, crash/power-loss durability, and multi-node operation remain unverified and unsupported.

## Findings

### High: unauthenticated sample read/write routes - fixed

The sample host exposed `/demo/{bucket}/{key}` routes that bypassed S3 authentication and allowed bucket creation, upload, and download. Exploitation required network access to that sample. The routes were removed; the sample now exposes the authenticated S3 endpoint and a non-sensitive status response.

### Medium: unsigned Amazon headers could affect metadata - fixed

SigV4 verification authenticated declared signed headers but previously accepted an additional unsigned `x-amz-meta-*` header. A client, or an on-path attacker on cleartext HTTP, could alter stored metadata without invalidating the signature. All `x-amz-*` headers except `x-amz-content-sha256` must now appear in `SignedHeaders`, consistent with AWS's signing rules. TLS remains mandatory.

Regression evidence: real Kestrel rejects unsigned metadata with `AuthorizationHeaderMalformed` and commits no object.

### Medium: continuation tokens lacked expiry - fixed

S3 HMAC tokens and application Data Protection tokens were parameter-bound and tamper resistant but lived for the key lifetime. S3 tokens were transferable between credentials with equivalent list permission. S3 tokens now contain a signed expiry and issuing access-key identity. API tokens use a time-limited Data Protector. Both endpoints authorize the current request before decoding or using a token.

Regression evidence covers tampering, changed parameters, bucket/scope changes, another S3 credential, denied current application authorization, and expiry.

### Medium: incomplete resource admission - fixed

Object and list size were bounded, but key, query, aggregate header, metadata, token, concurrency, and queue sizes were not all library-controlled. Startup-validated limits now cover those inputs. Fixed concurrency and bounded queues protect expensive S3/API operations. Overload returns S3 `503 SlowDown` or API `503 service_unavailable`. Streaming is preserved.

Regression evidence confirms overload rejection, permit recovery after cancellation, empty staging, and rejection of oversized or header-unsafe object properties before blob creation.

### Low: direct metadata could be unsafe for response headers - fixed

The core now rejects null/control characters in keys, CR/LF in content types and metadata values, invalid metadata names, and configured count/size overruns. Existing rows inserted outside the library remain an operator responsibility.

### Informational: SigV4 replay window - accepted

Header-authenticated requests remain valid while `x-amz-date` is within `AllowedClockSkew` (15 minutes by default). This is not one-time request protection. Captured requests can be replayed in that interval subject to current configured grants. Require TLS, minimize skew, and make PUT/DELETE callers replay-tolerant. Credential options are startup snapshots; rotation or revocation requires configuration replacement and application restart.

### Informational: local path substitution - deployment responsibility

Generated blob IDs prevent traversal-shaped, absolute-looking, Unicode, slash, or backslash keys from selecting paths. The lock rejects a second cooperating engine. A privileged local attacker who can replace root components with symlinks, junctions, or reparse points is outside the model. Storage, Data Protection, configuration, and backup paths must be owned by the service/backup operator and not writable by untrusted users.

## S3 Authentication Review

Inspection covered canonical method, host, raw escaped path, sorted encoded query name/value pairs including repeats, sorted lower-case signed headers, whitespace collapse, payload hash, date/region/service scope, malformed fields, and fixed-time comparison. AWS SDK tests cover Unicode and special-character keys, ranges, and listings. AWS CLI remains a compatibility target but was unavailable on PATH in this run.

PUT supports a 64-hex SHA-256 payload hash. Streaming validation finishes before staging promotion. A failed hash, cancellation, or source read cannot replace a committed object and staging is cleaned. GET/HEAD/DELETE/List accept an empty SHA-256 or `UNSIGNED-PAYLOAD`. `aws-chunked`, trailers, presigned query auth, session credentials, multipart, and extra checksum modes remain unsupported and fail explicitly.

Authentication, bucket/operation authorization, query/header validation, and admission precede bucket lookup and object I/O. `ListBucket` is a separate grant. Signature timestamp checks do not provide replay prevention.

## Authorization and Browser Delivery

The API route group requires host authentication and defaults to deny-all authorization. Browse, metadata, download, view, HEAD, and range each request an operation-specific grant. Bucket and physical prefix are server-derived. Anonymous requests return `401`; authenticated denial and missing objects both return `404` to reduce existence disclosure.

Tokens bind logical scope, bucket, prefix, listing parameters, and page size, expire, and are used only after current authorization. Inputs such as `../`, leading separators, percent signs, and backslashes remain literal text beneath the granted prefix.

Downloads use sanitized content disposition. Inline view is limited to an explicit media-type allowlist; HTML, SVG, unknown, and invalid types are attachments. Responses set `nosniff` and `private, no-store`.

The library does not configure CORS or antiforgery. Cookie hosts must restrict origins and protect state-changing application routes. Packaged API endpoints are read-only; S3 uses SigV4 rather than browser cookies.

ReportStudio registers testing-header authentication only when the host environment is exactly `Testing`, maps development sign-in only in `Development`, and uses cookie authentication otherwise. Production-boundary tests confirm ordinary configuration cannot register test identities or development routes. Environment selection is privileged deployment state: starting as `Testing` deliberately selects that boundary.

## Filesystem, PostgreSQL, Backup, and Errors

PostgreSQL values are parameters. The startup-validated, quoted schema identifier is limited to 63 UTF-8 bytes. Listings cap results at 1000 plus one look-ahead row. Runtime roles should have only connect/schema usage and required DML; use separate provisioning and backup roles where practical.

Protocol error bodies are fixed and exclude SQL, paths, connection strings, credentials, signatures, tokens, authorization headers, and content. Unexpected exception details can enter protected host logs; do not enable header/body logging on these routes. Backup passwords use `PGPASSWORD`, not command arguments. Tool output and backups are sensitive.

Backup paths are lexically constrained and inventory files are hash/length checked. Manifests provide consistency, not origin authenticity. Restore accepts only an absent schema and new root. Backups must be operator-controlled; hostile reparse points are not supported.

## Verification Classification

- Code inspection: shipping projects, routes, SigV4, tokens, authorization, filesystem mapping, SQL, browser headers, backup/restore, sample, and ReportStudio gates.
- Automated: Release tests, real Kestrel, isolated real PostgreSQL schemas, AWS SDK, package-only consumption, ReportStudio production boundary, overload/cancellation, package inspection, and advisory audit.
- Manual: source/diff and artifact review, deployment-document review, and pilot status checks.
- Untested here: AWS CLI (unavailable), rc.2 two-hour soak, hostile local reparse-point manipulation, shared hosting/network filesystems, power loss, and external penetration testing.

## Residual Risks

Compromised credentials work until restart with new configuration. Signed requests replay within clock skew. HTTP admission does not replace Kestrel/proxy connection, rate, timeout, OS, PostgreSQL pool, or egress limits; slow downloads can hold a permit. Filesystem and database commit are not a distributed transaction, and cleanup reclaims unreferenced blobs. Backups are not encrypted or signed by this library. Operators must provide TLS, secure secrets, trusted proxies, least privilege, protected persistent directories, monitoring, and tested restores.

## Primary References

- AWS, [Signature Calculations for the Authorization Header](https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-header-based-auth.html)
- Microsoft, [Proxy servers and load balancers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer)
- Microsoft, [KestrelServerLimits](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits)
- PostgreSQL, [pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html)
