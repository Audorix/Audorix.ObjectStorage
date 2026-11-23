# Architecture and Reuse Decision

Reviewed on 2026-09-29:

- S3Server NuGet 7.3.2, package repository commit `03a7177acd605a32416184fda076d3e518816bf9`.
- AWSSignatureGenerator 1.1.0, package repository commit `5c23c036a5c2d8bc30c286cdd8304de64487c68b`.
- S3Server request parsing, authentication, routing, XML errors, and response path at the pinned source revision.
- AWS SDK for .NET S3 4.0.103.4 behavior and checksum configuration.

## Decision

The adapter uses a bounded, Audorix-owned ASP.NET Core protocol layer. S3Server and Watson are no longer runtime dependencies.

S3Server's reusable-looking protocol objects are coupled to `WatsonWebserver.Core` request, response, URL, and context types. Its server-side signed-payload path reads the complete body as bytes before constructing `V4SignatureResult`. That conflicts with both the existing ASP.NET Core listener requirement and bounded-memory uploads.

AWSSignatureGenerator correctly implements canonical SigV4 generation, but its ordinary signed-payload API hashes a supplied byte array or seekable stream. It has no API accepting a precomputed payload hash while independently validating the body as it streams into Audorix storage. Its streaming helpers target `aws-chunked`, which is outside this slice.

The implementation therefore contains only the required SigV4 header verifier and S3 error writer. It follows the AWS canonical request and signing-key algorithm, validates the advertised SHA-256 while the core engine copies the request into staging, and compares signatures and payload hashes in constant time. No upstream source was copied or adapted.

## Boundaries

- Protocol types stay in `Audorix.ObjectStorage.S3`; the core API remains database- and S3-neutral.
- The endpoint delegates every storage operation to `IObjectStorageEngine`.
- The endpoint maps into `IEndpointRouteBuilder` and starts no listener of its own.
- Credentials carry exact bucket and operation permissions.
- The endpoint path, method, host, query, and declared signed headers are included in authentication.
- Unsupported query operations, checksum modes, methods, and addressing modes fail explicitly.

PostgreSQL remains the first production metadata provider. Filesystem staging, atomic publish-before-metadata, per-key mutation serialization, active-reader tracking, and the single-root process lock remain unchanged.
