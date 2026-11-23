# Package Quick Start

## Packages

Install the packages needed by the host. `Audorix.ObjectStorage` and a metadata provider are required; endpoint packages are optional.

```xml
<ItemGroup>
  <PackageReference Include="Audorix.ObjectStorage" Version="0.1.0-rc.2" />
  <PackageReference Include="Audorix.ObjectStorage.PostgreSql" Version="0.1.0-rc.2" />
  <PackageReference Include="Audorix.ObjectStorage.S3" Version="0.1.0-rc.2" />
  <PackageReference Include="Audorix.ObjectStorage.Api" Version="0.1.0-rc.2" />
</ItemGroup>
```

All packages target `net10.0`. The ASP.NET Core endpoint packages use the shared `Microsoft.AspNetCore.App` framework and do not start a listener.

## Registration

```csharp
builder.Services
    .AddAudorixObjectStorage(options => options.StorageRootPath = storageRoot)
    .AddAudorixObjectStoragePostgreSql(options =>
    {
        options.ConnectionString = connectionString;
        options.SchemaName = "audorix_object_storage";
    })
    .AddAudorixObjectStorageS3(options =>
    {
        options.Region = "us-east-1";
        options.ContinuationTokenSigningKey = tokenSigningKey;
        options.Credentials.Add(new S3Credential
        {
            AccessKeyId = accessKey,
            SecretAccessKey = secretKey,
            AllowedBuckets = ["documents"],
            AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
        });
    });

builder.Services.AddAuthentication().AddCookie();
builder.Services.AddAuthorization();
builder.Services.AddAudorixObjectStorageApi();
builder.Services.AddSingleton<IObjectStorageApiAuthorization, ApplicationStorageAuthorization>();

app.UseAuthentication();
app.UseAuthorization();
app.MapAudorixObjectStorageS3("/s3");
app.MapAudorixObjectStorageApi("/object-storage");
```

Resolve `IObjectStorageEngine` for direct in-process create, put, list, read, range, and delete operations. Direct calls do not use HTTP. The S3 endpoint is for remote S3 clients and shares the application's listener. The authenticated API is for application identities; its default authorization implementation denies all requests, so the host must explicitly map each logical scope to a bucket and key prefix.

## Client Settings

AWS SDK for .NET clients must use the complete mapped URL, path-style addressing, the configured region, `RequestChecksumCalculation.WHEN_REQUIRED`, `ResponseChecksumValidation.WHEN_REQUIRED`, and `UseChunkEncoding = false` on uploads. AWS CLI must set `AWS_REQUEST_CHECKSUM_CALCULATION=WHEN_REQUIRED` and `AWS_RESPONSE_CHECKSUM_VALIDATION=WHEN_REQUIRED`.

Multipart upload, virtual-hosted addressing, presigned URLs, bucket administration, versioning, ACLs, tagging, `aws-chunked`, checksum trailers, and multiple ranges are not supported in this RC.

## Capability Matrix

| Surface | Create/put | List | Read | Range/HEAD | Delete | Authorization |
| --- | --- | --- | --- | --- | --- | --- |
| Direct `IObjectStorageEngine` | Yes | Yes | Yes | Yes | Yes | Host process owns access |
| S3 endpoint | PutObject only | ListObjectsV2 | GetObject | Single range and HeadObject | Yes | SigV4 credential bucket/operation scope |
| Application API | No | Yes | Download/view | Single range and HEAD | No | ASP.NET auth plus required host scope mapper |

See [deployment-guide.md](deployment-guide.md) before selecting a storage root or exposing either endpoint.
