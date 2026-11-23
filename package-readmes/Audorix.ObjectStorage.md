# Audorix.ObjectStorage

Provider-independent object storage contracts and a filesystem byte store for .NET 10. Metadata persistence is supplied by a separate provider package; PostgreSQL is the production implementation in this release candidate.

```csharp
using Audorix.ObjectStorage;
using Audorix.ObjectStorage.DependencyInjection;

builder.Services.AddAudorixObjectStorage(options =>
    options.StorageRootPath = builder.Configuration["ObjectStorage:RootPath"]
        ?? throw new InvalidOperationException("ObjectStorage:RootPath is required."));

IObjectStorageEngine storage = app.Services.GetRequiredService<IObjectStorageEngine>();
await storage.CreateBucketAsync("documents");
await using FileStream content = File.OpenRead("invoice.pdf");
await storage.PutObjectAsync(new PutObjectRequest("documents", "2026/invoice.pdf", content, "application/pdf"));
ObjectListPage page = await storage.ListObjectsAsync("documents", "2026/");
```

The configured directory must be persistent and writable by the application identity. Run only one storage-engine instance against a storage root. A shared metadata database does not make filesystem blobs available to multiple nodes.

Licensed under the MIT License. See the repository for the license text and third-party notices.
