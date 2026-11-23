# Audorix.ObjectStorage.S3

Maps a bounded S3-compatible endpoint into an application's existing ASP.NET Core listener.

```csharp
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;

builder.Services.AddAudorixObjectStorageS3(options =>
{
    options.Region = "us-east-1";
    options.ContinuationTokenSigningKey = configuration["S3:TokenKey"]!;
    options.Credentials.Add(new S3Credential
    {
        AccessKeyId = configuration["S3:AccessKey"]!,
        SecretAccessKey = configuration["S3:SecretKey"]!,
        AllowedBuckets = ["documents"],
        AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
    });
});

app.MapAudorixObjectStorageS3("/s3");
```

Clients must use path-style addressing, the configured region, and the endpoint path prefix. AWS SDK for .NET uploads must disable chunked encoding and use request checksums only when required. Multipart upload, virtual-hosted bucket addressing, presigned URLs, versioning, ACLs, tagging, and bucket administration are unsupported.

This adds routes to the application's listener; it does not open another port or run a service. Shared-hosting support is not claimed.

Licensed under the MIT License. See the repository for the license text and third-party notices.
