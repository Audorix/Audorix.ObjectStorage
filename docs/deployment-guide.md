# Deployment Guide

Security-sensitive deployments must also follow [security-hardening-guide.md](security-hardening-guide.md). Findings and residual risks are recorded in [security-review.md](security-review.md).

## Topology and Permissions

Object bytes are stored under `ObjectStorageOptions.StorageRootPath`; PostgreSQL stores metadata only. The application identity needs read, write, create, rename, delete, and exclusive file-lock permission on the root, `blobs`, and `staging`. Put the root and ASP.NET Core Data Protection keys on persistent storage outside directories replaced during deployment.

Run exactly one active `IObjectStorageEngine` against a storage root. Application recycling is supported after the old instance releases the root lock. Overlapped recycling, web-garden workers, horizontal replicas, or multiple containers pointed at one root are unsupported. A shared PostgreSQL database does not share filesystem bytes and does not make the service multi-node capable.

SQLite is neither required nor included, so SQLite locking is not runtime behavior in this RC. A future SQLite provider must establish journal mode, filesystem compatibility, recycling, and lock behavior separately. SQL Server remains deferred.

No administrator privileges, Docker daemon, background worker, or additional listener is required. S3 and application routes are mapped into the host's existing ASP.NET Core pipeline. Local Kestrel is verified; no commercial shared-hosting environment has been verified, so shared-hosting S3 support is not claimed.

## Reverse Proxy and Recycling

Preserve the mapped S3 path exactly because it is part of SigV4 canonicalization. Configure proxy request-size and timeout limits consistently with `S3EndpointOptions.MaxObjectSize`. Drain mutations before recycling where possible. Interrupted uploads remain uncommitted, while a previously committed object remains readable.

Persist Data Protection keys for application API continuation tokens. A recycled instance without the same keys cannot accept tokens issued by the prior instance; clients must restart enumeration.

## Credentials and Rotation

Load S3 access keys, secret keys, and continuation-token signing keys from the host's secret provider. Never place them in appsettings committed to source, package readmes, command lines, or browser code. Application API authorization is separate from S3 credentials and should derive grants from the authenticated host identity.

Rotate an S3 credential by temporarily configuring both old and new access-key pairs with identical least-privilege bucket/operation scopes, deploying, moving clients to the new pair, confirming old-key traffic has stopped, then removing the old pair and recycling the single instance. Access-key IDs must be unique. Rotate the continuation-token signing key during a maintenance window; existing list tokens become invalid and clients restart listing.

## Backup and Restore

Treat the PostgreSQL schema and filesystem root as one recovery set. Use the maintenance lease to reject new mutations and wait for admitted mutations before capturing both. Validate the manifest and hashes before restore, restore into a clean schema and clean root, and start only one engine after both halves are present. See [backup-and-restore.md](backup-and-restore.md) for commands and failure handling.

## Production Checklist

1. Supply the project license before publishing packages externally.
2. Use a dedicated PostgreSQL schema and least-privilege role.
3. Provision a persistent root and durable Data Protection key directory with the required permissions.
4. Disable development/test authentication and register a production identity provider.
5. Register an explicit `IObjectStorageApiAuthorization`; the built-in default denies all access.
6. Scope each S3 credential to required buckets and operations, then test rotation and revocation.
7. Verify proxy limits, restart behavior, backup/restore, and filesystem durability on the actual host.
8. Keep the deployment single-instance. Do not infer multi-node support from PostgreSQL availability.
