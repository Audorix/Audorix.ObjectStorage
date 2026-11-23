# First-Slice Completion Report

## Implemented

- Independent .NET 10 solution and class libraries.
- Direct API for bucket create/inspect, object put/get/head/list/delete, and safe overwrite.
- Provider-based metadata contract independent of database-specific public types.
- PostgreSQL metadata provider as the first production implementation.
- Filesystem blob storage using generated blob IDs, not object keys.
- Upload staging before publish and metadata commit after publish.
- Per-key mutation serialization and single active engine instance per storage root.
- Active-reader tracking and cleanup that avoids deleting blobs currently being read.
- ASP.NET Core host demonstrating direct embedded usage through normal application routing.

## Tested

- Byte-for-byte round trips and metadata preservation.
- Persistence after engine disposal and reopening using a test metadata provider.
- Empty objects and Unicode/special-character/traversal-like keys.
- Prefix listing and bounded pagination.
- Overwrite while a previous read is active.
- Failed upload preserving the old committed object.
- Concurrent mutations to the same key.
- Rejection of a second engine instance for the same storage root.
- S3 shared-hosting support is explicitly not claimed.

## Deferred

- SQLite embedded metadata provider.
- SQL Server metadata provider for Windows/shared-hosting deployments.
- Multipart uploads, presigned URLs, versioning, replication, distributed locks, dashboard, and advanced IAM.
- PostgreSQL live integration tests requiring a real database.
- S3 authenticated round trips through an ASP.NET Core pipeline adapter.

## Blocked

S3 shared-hosting endpoint integration is blocked. S3Server 7.3.2 is documented as starting its own Watson listener with `server.Start()`. No verified ASP.NET Core endpoint-routing adapter was found in this slice, and a fake S3 endpoint was intentionally not substituted.

## Durability Boundary

The tested durability boundary is process disposal/reopen. This slice does not claim power-loss durability. For production, place the storage root on durable storage, back up the PostgreSQL database and blob directory consistently, and keep one active engine per blob root.
