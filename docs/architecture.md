# Architecture overview

Audorix.ObjectStorage is an embedded .NET object store. An ASP.NET Core application owns the process and listener; optional HTTP endpoints are mapped into that application.

## Components

| Component | Responsibility |
| --- | --- |
| `Audorix.ObjectStorage` | Storage contracts, filesystem blobs, staged writes, range reads, and lifecycle coordination |
| `Audorix.ObjectStorage.PostgreSql` | Bucket/object metadata persistence and backup coordination |
| `Audorix.ObjectStorage.S3` | Path-style S3-compatible routes and SigV4 header authentication |
| `Audorix.ObjectStorage.Api` | Host-authenticated logical-scope browse and download endpoints |

Object bytes are written under the configured storage root. PostgreSQL stores bucket and object metadata, including opaque blob identifiers. A successful write stages new bytes and only then updates metadata. The engine retains active-reader tracking so a blob being read is not removed during overwrite or cleanup.

## Request paths

- **In-process:** application code resolves `IObjectStorageEngine` and calls it directly.
- **S3 clients:** the host maps `MapAudorixObjectStorageS3` at a path such as `/s3`; clients sign that complete path.
- **Application users:** the host maps `MapAudorixObjectStorageApi` and supplies `IObjectStorageApiAuthorization` to map trusted user identity to an internal bucket/key prefix.

## Deployment boundary

The current filesystem provider supports one active engine per storage root. PostgreSQL is not a shared blob store, and multiple nodes with separate disks do not form a cluster. Backup and restore must capture PostgreSQL metadata and the filesystem root as one recovery set.

## Extensibility

`IObjectStorageMetadataStore` allows another metadata provider to be supplied by a separate package. PostgreSQL is the only production provider included today. Blob storage is filesystem-backed; alternate blob providers are not currently included.

See [deployment guide](deployment-guide.md) for operational constraints and [S3 compatibility](s3-compatibility-matrix.md) for protocol behavior.
