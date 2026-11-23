using Audorix.ObjectStorage;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("ObjectStorage")
    ?? "Host=localhost;Port=54329;Database=audorix_demo;Username=audorix_demo;Password=local-demo-only";
var storageRoot = builder.Configuration["ObjectStorage:RootPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "objects");
var accessKey = builder.Configuration["S3:AccessKeyId"] ?? "local-demo-key";
var secretKey = builder.Configuration["S3:SecretAccessKey"] ?? "local-demo-secret-change-before-use";
var tokenKey = builder.Configuration["S3:TokenSigningKey"]
    ?? "local-demo-token-signing-key-at-least-32-bytes";

builder.Services
    .AddAudorixObjectStorage(options => options.StorageRootPath = storageRoot)
    .AddAudorixObjectStoragePostgreSql(options =>
    {
        options.ConnectionString = connectionString;
        options.SchemaName = "audorix_demo";
    })
    .AddAudorixObjectStorageS3(options =>
    {
        options.Region = "us-east-1";
        options.ContinuationTokenSigningKey = tokenKey;
        options.Credentials.Add(new S3Credential
        {
            AccessKeyId = accessKey,
            SecretAccessKey = secretKey,
            AllowedBuckets = ["demo"],
            AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
        });
    });

var app = builder.Build();
const string bucket = "demo";
await app.Services.GetRequiredService<IObjectStorageEngine>().CreateBucketAsync(bucket);

app.MapGet("/", () => Results.Content("""
    <!doctype html>
    <html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
    <title>Audorix ObjectStorage Demo</title>
    <style>
    body{font:16px system-ui;max-width:850px;margin:3rem auto;padding:0 1rem;color:#202c3c}
    h1{color:#175f72}section{border:1px solid #d8e2e8;border-radius:12px;padding:1.2rem;margin:1rem 0}
    button{background:#176a7e;color:white;border:0;border-radius:6px;padding:.6rem 1rem;cursor:pointer}
    li{margin:.6rem 0}small{color:#5e6d7b}pre{white-space:pre-wrap;background:#f3f7f9;padding:1rem}
    </style>
    <h1>Audorix.ObjectStorage</h1>
    <p>Local preview demo: ASP.NET Core API, filesystem object bytes, PostgreSQL metadata, and an S3 endpoint.</p>
    <section><h2>Upload an object</h2><input id="file" type="file"><button onclick="upload()">Upload</button><p id="message"></p></section>
    <section><h2>Objects in demo bucket</h2><button onclick="refresh()">Refresh</button><ul id="objects"></ul></section>
    <section><h2>Try an S3 client</h2><pre>Endpoint: http://localhost:5080/s3
    Access key: local-demo-key
    Secret key: local-demo-secret-change-before-use
    Bucket: demo
    Region: us-east-1</pre><small>Local demo credentials only. Never expose this host outside your machine.</small></section>
    <script>
    async function refresh(){const r=await fetch('/demo/objects');const items=await r.json();document.querySelector('#objects').innerHTML=items.map(x=>`<li><a href="/demo/objects/${encodeURIComponent(x.key)}">${x.key}</a> — ${x.size} bytes</li>`).join('')||'<li>No objects uploaded yet.</li>'}
    async function upload(){const f=document.querySelector('#file').files[0];if(!f)return;const r=await fetch('/demo/objects/'+encodeURIComponent(f.name),{method:'PUT',headers:{'Content-Type':f.type||'application/octet-stream'},body:f});document.querySelector('#message').textContent=r.ok?'Upload complete.':'Upload failed: '+await r.text();await refresh()}
    refresh();
    </script>
    </html>
    """, "text/html"));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/demo/objects", async (IObjectStorageEngine storage, CancellationToken ct) =>
{
    var page = await storage.ListObjectsAsync(bucket, pageSize: 1000, cancellationToken: ct);
    return Results.Ok(page.Objects.Select(item => new { item.Key, item.Size, item.ContentType, item.ModifiedAt }));
});
app.MapPut("/demo/objects/{**key}", async (string key, HttpRequest request, IObjectStorageEngine storage, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest("An object key is required.");
    var result = await storage.PutObjectAsync(new PutObjectRequest(bucket, key, request.Body, request.ContentType), ct);
    return Results.Created($"/demo/objects/{Uri.EscapeDataString(result.Key)}", new { result.Key, result.Size });
});
app.MapGet("/demo/objects/{**key}", async (string key, IObjectStorageEngine storage, CancellationToken ct) =>
{
    var result = await storage.OpenReadAsync(bucket, key, ct);
    return Results.Stream(result.Content, result.Object.ContentType ?? "application/octet-stream", enableRangeProcessing: true);
});

app.MapAudorixObjectStorageS3("/s3");
await app.RunAsync();
