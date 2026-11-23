# Audorix.ObjectStorage
**A .NET 10 object store with an S3-compatible endpoint, filesystem-backed object bytes, and PostgreSQL metadata.**

[Source code](https://github.com/audorix/Audorix.ObjectStorage) · [Issues](https://github.com/audorix/Audorix.ObjectStorage/issues) · [Releases](https://github.com/audorix/Audorix.ObjectStorage/releases)

Audorix.ObjectStorage is an embeddable object storage engine for ASP.NET Core applications. It maps S3-compatible routes into the host's existing listener, stores object bytes on disk, and stores bucket/object metadata through a provider interface. PostgreSQL is the current production metadata provider.

> **Project status:** public preview (`0.1.0-rc.2`). The supported S3 surface is intentionally limited. This is a single-instance deployment: one active engine per storage root. Production/shared-hosting readiness and multi-node operation are not claimed.

## What it includes

- Direct .NET API for buckets, streaming object writes/reads, byte ranges, listing, and deletion.
- Filesystem blob storage with staged writes and atomic commit behavior.
- PostgreSQL metadata provider and coordinated backup support.
- Optional authenticated ASP.NET Core API with deny-by-default host authorization.
- Optional path-style S3-compatible endpoint using SigV4 header authentication.
- Explicit errors for unsupported S3 operations instead of silent approximations.

## Packages

| Package | Purpose |
| --- | --- |
| `Audorix.ObjectStorage` | Core contracts and filesystem blob engine |
| `Audorix.ObjectStorage.PostgreSql` | PostgreSQL metadata provider |
| `Audorix.ObjectStorage.S3` | S3 protocol endpoints for ASP.NET Core |
| `Audorix.ObjectStorage.Api` | Authenticated application-facing browse/download API |

All packages target .NET 10. See [the package quick start](docs/package-quick-start.md), [S3 compatibility matrix](docs/s3-compatibility-matrix.md), and [deployment guide](docs/deployment-guide.md).

## Quick start

Register storage and PostgreSQL, then map the optional S3 endpoint. Keep credentials in a secret provider or environment variables.

```csharp
builder.Services
    .AddAudorixObjectStorage(options =>
    {
        options.StorageRootPath = Path.Combine(
            builder.Environment.ContentRootPath, "App_Data", "objects");
    })
    .AddAudorixObjectStoragePostgreSql(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("ObjectStorage")!;
        options.SchemaName = "audorix_object_storage";
    })
    .AddAudorixObjectStorageS3(options =>
    {
        options.Region = "us-east-1";
        options.ContinuationTokenSigningKey = builder.Configuration["S3:TokenSigningKey"]!;
        options.Credentials.Add(new S3Credential
        {
            AccessKeyId = builder.Configuration["S3:AccessKeyId"]!,
            SecretAccessKey = builder.Configuration["S3:SecretAccessKey"]!,
            AllowedBuckets = ["documents"],
            AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
        });
    });

var app = builder.Build();
app.MapAudorixObjectStorageS3("/s3");
app.Run();
```

Provision buckets through the in-process engine:

```csharp
IObjectStorageEngine storage = app.Services.GetRequiredService<IObjectStorageEngine>();
await storage.CreateBucketAsync("documents");
```

AWS SDK for .NET clients must use path-style addressing, the matching signing region, `RequestChecksumCalculation.WHEN_REQUIRED`, `ResponseChecksumValidation.WHEN_REQUIRED`, and `UseChunkEncoding = false` for uploads. See [client configuration](docs/package-quick-start.md#client-settings).

## Run the demo

The local-only demo starts a PostgreSQL container and an ASP.NET Core host. It demonstrates HTTP upload/download/listing and the S3-compatible endpoint. Do not expose its sample credentials outside your machine.

```powershell
docker compose -f samples/Audorix.ObjectStorage.Demo/compose.yaml up --build
```

Open `http://localhost:5080/` for the demo controls. Full steps and AWS CLI examples are in [the demo guide](samples/Audorix.ObjectStorage.Demo/README.md).

## Important limits

- One active engine instance per filesystem storage root; no horizontal/multi-node mode.
- PostgreSQL stores metadata; object bytes remain on the configured filesystem.
- S3 support is path-style and limited to the operations in the [compatibility matrix](docs/s3-compatibility-matrix.md).
- Multipart upload, presigned URLs, virtual-hosted addressing, bucket administration, versioning, ACLs, and checksum trailers are not implemented.
- The endpoint joins the host's existing ASP.NET Core pipeline; it does not create a separate listener.

Read the [security hardening guide](docs/security-hardening-guide.md) and [deployment guide](docs/deployment-guide.md) before exposing an endpoint to a network.

## Documentation

- [Documentation index](docs/README.md)
- [Architecture and component guide](docs/architecture.md)
- [API integration](docs/api-integration.md)
- [Deployment and operations](docs/deployment-guide.md)
- [Backup and restore](docs/backup-and-restore.md)
- [Security policy](SECURITY.md)
- [Contributing](CONTRIBUTING.md)
- [Launch page copy](docs/open-source-launch-page.md)

## Build and verify

Requires the .NET 10 SDK. PostgreSQL-backed tests require a local PostgreSQL instance; see [contributing](CONTRIBUTING.md).

```powershell
dotnet restore Audorix.ObjectStorage.slnx
dotnet build Audorix.ObjectStorage.slnx --configuration Release --no-restore
dotnet test tests/Audorix.ObjectStorage.Tests/Audorix.ObjectStorage.Tests.csproj --configuration Release
```

## License

Audorix.ObjectStorage is licensed under the [MIT License](LICENSE). Third-party component notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
