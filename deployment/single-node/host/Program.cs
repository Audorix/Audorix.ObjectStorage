using System.Diagnostics;
using System.Text.Json;
using Audorix.ObjectStorage;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.PostgreSql;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

const long MiB = 1024L * 1024L;
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsProduction()) throw new InvalidOperationException("The deployment host runs only in Production.");

IConfiguration c = builder.Configuration;
string Required(string key) => c[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configuration '{key}' is required.");
string root = Path.GetFullPath(Required("Deployment:StorageRoot"));
string keys = Path.GetFullPath(Required("Deployment:DataProtectionRoot"));
string identity = Required("Deployment:Identity");
string bucket = Required("S3:Bucket");
string connection = Required("ConnectionStrings:AudorixObjectStorage");
string schema = Required("PostgreSql:Schema");
bool initialize = c.GetValue("Deployment:AllowInitialize", false);
long maxObject = c.GetValue("Limits:MaxObjectBytes", 64 * MiB);
int active = c.GetValue("Limits:MaxConcurrentOperations", 8);
int queued = c.GetValue("Limits:MaxQueuedOperations", 4);
if (maxObject is < 1 or > 64 * MiB || active is < 1 or > 8 || queued is < 0 or > 4)
    throw new InvalidOperationException("Configured admission limits exceed the validated deployment envelope.");

builder.WebHost.ConfigureKestrel(o => {
    o.Limits.MaxRequestBodySize = maxObject;
    o.Limits.MaxRequestHeaderCount = 100;
    o.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddDataProtection().SetApplicationName("Audorix.ObjectStorage.SingleNode")
    .PersistKeysToFileSystem(new DirectoryInfo(keys));
builder.Services.AddAudorixObjectStorage(o => {
    o.StorageRootPath = root; o.MaxKeyLengthBytes = 1024; o.MaxMetadataEntries = 50;
    o.MaxMetadataBytes = 8192; o.MaxContentTypeLength = 256;
}).AddAudorixObjectStoragePostgreSql(o => { o.ConnectionString = connection; o.SchemaName = schema; })
  .AddAudorixObjectStoragePostgreSqlBackup()
  .AddAudorixObjectStorageS3(o => {
      o.Region = c["S3:Region"] ?? "us-east-1"; o.ContinuationTokenSigningKey = Required("S3:ContinuationTokenSigningKey");
      o.MaxObjectSize = maxObject; o.MaxConcurrentOperations = active; o.MaxQueuedOperations = queued;
      o.MaxKeyLengthBytes = 1024; o.MaxQueryStringBytes = 8192; o.MaxHeaderCount = 100; o.MaxHeaderBytes = 32768;
      o.MaxContinuationTokenLength = 8192; o.ContinuationTokenLifetime = TimeSpan.FromMinutes(15);
      o.Credentials.Add(new S3Credential { AccessKeyId = Required("S3:AccessKeyId"), SecretAccessKey = Required("S3:SecretAccessKey"),
          AllowedBuckets = new HashSet<string>([bucket], StringComparer.Ordinal), AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket });
  });

string[] proxies = c.GetSection("Network:KnownProxies").Get<string[]>() ?? [];
if (proxies.Length > 0) builder.Services.Configure<ForwardedHeadersOptions>(o => {
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1; o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();
    foreach (string proxy in proxies) o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
});

WebApplication app = builder.Build();
if (proxies.Length > 0) app.UseForwardedHeaders();
Directory.CreateDirectory(root); Directory.CreateDirectory(keys);
string marker = Path.Combine(root, ".audorix-single-node-identity");
if (initialize) {
    if (File.Exists(marker) && await File.ReadAllTextAsync(marker) != identity) throw new InvalidOperationException("Storage identity mismatch.");
    if (!File.Exists(marker)) await File.WriteAllTextAsync(marker, identity);
} else if (!File.Exists(marker) || await File.ReadAllTextAsync(marker) != identity) throw new InvalidOperationException("Persistent storage is missing or has the wrong identity. Run explicit provisioning; refusing an empty root.");

IObjectStorageEngine engine = app.Services.GetRequiredService<IObjectStorageEngine>();
BucketInfo? existing = await engine.GetBucketAsync(bucket);
if (existing is null && initialize) await engine.CreateBucketAsync(bucket);
else if (existing is null) throw new InvalidOperationException("Configured bucket is absent. Refusing to initialize a possibly different database.");
if (args.FirstOrDefault() == "provision") return;
if (args.FirstOrDefault() == "backup") {
    string destination = Required("Backup:Destination");
    await app.Services.GetRequiredService<PostgreSqlObjectStorageBackupService>().CreateAsync(destination);
    return;
}
if (args.FirstOrDefault() == "restore") {
    await app.Services.GetRequiredService<PostgreSqlObjectStorageBackupService>().RestoreAsync(
        Required("Restore:BackupPath"), Required("Restore:StorageRoot"), Required("Restore:ConnectionString"));
    return;
}

DeploymentMetrics metrics = new();
app.Use(async (ctx, next) => {
    if (!ctx.Request.Path.StartsWithSegments("/s3")) { await next(); return; }
    long started = Stopwatch.GetTimestamp(); metrics.Start();
    try { await next(); metrics.Finish(ctx.Response.StatusCode, Stopwatch.GetElapsedTime(started)); }
    catch { metrics.Fail(Stopwatch.GetElapsedTime(started)); throw; }
});
app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
app.MapGet("/health/ready", async () => {
    try {
        bool markerOk = File.Exists(marker) && await File.ReadAllTextAsync(marker) == identity;
        bool dbOk = await engine.GetBucketAsync(bucket) is not null;
        return markerOk && dbOk ? Results.Json(new { status = "ready" }) : Results.StatusCode(503);
    } catch { return Results.StatusCode(503); }
});
app.MapGet("/ops/metrics", (HttpContext ctx) => {
    string expected = Required("Operations:BearerToken");
    if (!ctx.Request.Headers.Authorization.ToString().Equals("Bearer " + expected, StringComparison.Ordinal)) return Results.Unauthorized();
    DriveInfo drive = new(Path.GetPathRoot(root)!); Process p = Process.GetCurrentProcess();
    return Results.Json(new { metrics.Active, metrics.Completed, metrics.Failures, metrics.Rejections, metrics.AverageLatencyMs,
        workingSetBytes = p.WorkingSet64, managedHeapBytes = GC.GetTotalMemory(false), freeDiskBytes = drive.AvailableFreeSpace,
        stagingFiles = Directory.Exists(Path.Combine(root, "staging")) ? Directory.EnumerateFiles(Path.Combine(root, "staging")).Count() : 0 });
});
app.MapAudorixObjectStorageS3("/s3");
await app.RunAsync();

sealed class DeploymentMetrics {
    long active, completed, failures, rejections, elapsedTicks;
    public long Active => Interlocked.Read(ref active); public long Completed => Interlocked.Read(ref completed);
    public long Failures => Interlocked.Read(ref failures); public long Rejections => Interlocked.Read(ref rejections);
    public double AverageLatencyMs => Completed == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref elapsedTicks) / Completed).TotalMilliseconds;
    public void Start() => Interlocked.Increment(ref active);
    public void Finish(int status, TimeSpan elapsed) { Interlocked.Decrement(ref active); Interlocked.Increment(ref completed); Interlocked.Add(ref elapsedTicks, elapsed.Ticks); if (status >= 500) Interlocked.Increment(ref failures); if (status is 413 or 429 or 503) Interlocked.Increment(ref rejections); }
    public void Fail(TimeSpan elapsed) { Interlocked.Decrement(ref active); Interlocked.Increment(ref completed); Interlocked.Increment(ref failures); Interlocked.Add(ref elapsedTicks, elapsed.Ticks); }
}
