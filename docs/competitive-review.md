# Competitive review and project positioning

Reviewed on 2026-11-23. This is a product-scope comparison, not a feature-by-feature certification. Upstream projects may change; use their linked repositories for current details.

## Similar projects

| Project | What it is | Strong launch patterns | How Audorix.ObjectStorage differs |
| --- | --- | --- | --- |
| [MinIO .NET SDK](https://github.com/minio/minio-dotnet) | A .NET client for MinIO and S3-compatible services | Fast start, direct and DI examples, runnable sample hosts, unit and integration test projects, Apache-2.0 license | Audorix is an embedded storage engine/server endpoint, not an S3 client library. |
| [S3Server](https://github.com/jchristn/S3Server) | A server-side S3 protocol layer that delegates storage/auth decisions to callbacks | Clear separation of protocol and backend, broad compatibility claims backed by tests, sample server, contribution and release docs | Audorix includes a filesystem blob engine, PostgreSQL metadata, and host-in-process endpoint packages. Its S3 protocol scope is much narrower and explicitly documented. |
| [Less3](https://github.com/jchristn/Less3) | A self-hosted S3-compatible object storage server | Demonstrates how an S3-compatible server can be exercised by common clients and provides a deployment-oriented product story | Audorix is designed to embed in an existing .NET app and share its listener; it is single-node and not a replacement for a distributed storage cluster. |
| [Blobject](https://github.com/jchristn/Blobject) | A common object/blob API over local and remote storage providers | Detailed runnable tests, provider examples, shared contract testing, telemetry and practical docs | Audorix currently focuses on owning a filesystem-backed object store with a separate metadata provider and limited S3 server facade. It does not currently offer provider breadth or built-in OpenTelemetry. |

## What to borrow

The current public entry point follows the strongest common practices: concise README, install/build examples, a runnable sample, a clear license, contribution and security guidance, and a protocol compatibility matrix. MinIO's host example and Less3's client-oriented validation also suggest that the local demo should be paired with real client commands; this repository's demo includes AWS CLI instructions.

S3Server and Blobject show a further step worth taking before 1.0: a reusable protocol/provider contract suite. The current xUnit project has useful engine, API, AWS SDK, and conditional PostgreSQL integration coverage; future work should separate portable deterministic tests from provider integration tests and run both in CI with explicit environment setup.

## Differentiated value proposition

Position Audorix.ObjectStorage as **an embeddable .NET object-storage engine for single-node ASP.NET Core deployments**, where an application wants filesystem-owned bytes, PostgreSQL metadata, optional application endpoints, and a bounded S3-compatible route without running a separate object-store process.

Avoid describing it as a complete S3 replacement, distributed object store, production-ready shared-hosting product, or drop-in AWS-compatible endpoint. Those would conflict with the current tested feature matrix and deployment constraints.

## Gaps to close after the initial public preview

1. Verify each compatibility claim against a named client/version and automate the core cases in CI.
2. Add a supported Linux CI path with PostgreSQL service coverage; a few existing backup tests currently assume Windows PostgreSQL tools and need platform-aware setup.
3. Add optional metrics/traces and health/readiness guidance while keeping instrumentation dependencies opt-in.
4. Decide the long-term support policy, stable release criteria, and issue response expectations before 1.0.
5. Publish a complete public roadmap for metadata-provider and blob-provider extension points, without implying these providers already exist.

## Sources

- MinIO repository overview, requirements, example projects, and license: https://github.com/minio/minio-dotnet
- S3Server architecture and stated protocol scope: https://github.com/jchristn/S3Server
- Less3 project overview: https://github.com/jchristn/Less3
- Blobject README, tests, provider breadth, and telemetry: https://github.com/jchristn/Blobject
