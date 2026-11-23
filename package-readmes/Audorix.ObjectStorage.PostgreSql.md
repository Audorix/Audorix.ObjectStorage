# Audorix.ObjectStorage.PostgreSql

PostgreSQL metadata persistence for `Audorix.ObjectStorage`. The public storage API remains independent of Npgsql and database-specific types.

```csharp
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;

builder.Services
    .AddAudorixObjectStorage(options => options.StorageRootPath = storageRoot)
    .AddAudorixObjectStoragePostgreSql(options =>
    {
        options.ConnectionString = connectionString;
        options.SchemaName = "audorix_object_storage";
    });
```

Use a dedicated PostgreSQL schema and initialize the resolved `IObjectStorageMetadataStore` before accepting traffic. Back up the PostgreSQL schema and filesystem root as one maintenance-protected recovery set. This package does not provide SQLite or SQL Server persistence.

This release candidate has no declared project license. Do not publish it to a public package feed until the owner supplies licensing terms.
