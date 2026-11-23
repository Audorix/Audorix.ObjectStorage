using Audorix.ObjectStorage;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestHeaderCount = 100;
    options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
});

builder.Services
    .AddAudorixObjectStorage(options =>
    {
        options.StorageRootPath = builder.Configuration.GetValue<string>("Audorix:ObjectStorage:StorageRootPath")
            ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "object-storage");
        options.MaxKeyLengthBytes = 1024;
        options.MaxMetadataEntries = 50;
        options.MaxMetadataBytes = 8 * 1024;
        options.MaxContentTypeLength = 256;
    })
    .AddAudorixObjectStoragePostgreSql(options =>
    {
        options.ConnectionString = builder.Configuration.GetConnectionString("AudorixObjectStorage")
            ?? throw new InvalidOperationException("Connection string 'AudorixObjectStorage' is required.");
        options.SchemaName = builder.Configuration["Audorix:ObjectStorage:PostgreSqlSchema"]
            ?? "audorix_object_storage";
    })
    .AddAudorixObjectStorageS3(options =>
    {
        options.Region = builder.Configuration.GetValue<string>("Audorix:S3:Region") ?? "us-east-1";
        options.ContinuationTokenSigningKey = builder.Configuration["Audorix:S3:ContinuationTokenSigningKey"]
            ?? throw new InvalidOperationException("Audorix:S3:ContinuationTokenSigningKey is required.");
        options.MaxObjectSize = 512L * 1024 * 1024;
        options.MaxKeyLengthBytes = 1024;
        options.MaxQueryStringBytes = 8 * 1024;
        options.MaxHeaderCount = 100;
        options.MaxHeaderBytes = 32 * 1024;
        options.MaxContinuationTokenLength = 8 * 1024;
        options.ContinuationTokenLifetime = TimeSpan.FromMinutes(15);
        options.MaxConcurrentOperations = 32;
        options.MaxQueuedOperations = 8;
        options.Credentials.Add(new S3Credential
        {
            AccessKeyId = builder.Configuration["Audorix:S3:AccessKeyId"]
                ?? throw new InvalidOperationException("Audorix:S3:AccessKeyId is required."),
            SecretAccessKey = builder.Configuration["Audorix:S3:SecretAccessKey"]
                ?? throw new InvalidOperationException("Audorix:S3:SecretAccessKey is required."),
            AllowedBuckets = (builder.Configuration["Audorix:S3:AllowedBuckets"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal),
            AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
        });
    });

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Audorix.ObjectStorage.Host",
    directStorage = "available through dependency injection",
    s3 = "path-style object operations mapped at /s3"
}));

app.MapAudorixObjectStorageS3("/s3");

app.Run();
