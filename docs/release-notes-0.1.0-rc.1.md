# 0.1.0-rc.1 Release Notes

This internal release candidate packages the provider-independent engine, PostgreSQL metadata provider, S3 endpoint, and authenticated application API as four `net10.0` NuGet packages. The executable host remains a sample and is not packable.

The package-only consumer demonstrates DI registration, PostgreSQL metadata in an isolated schema, filesystem bytes in an isolated root, direct operations, S3 over a real Kestrel listener, explicit application authorization, negative credential/scope behavior, and restart recovery. Package contents and dependency metadata are validated separately.

Known limits include single-instance filesystem ownership, no shared blob backend, no SQLite or SQL Server provider, no multipart/presigned/virtual-hosted S3 modes, and no verified shared-hosting deployment. See the package quick start and deployment guide for client settings and operational constraints.

Public publication is blocked because the source tree contains no project license. No license expression or legal attribution has been invented for these packages.
