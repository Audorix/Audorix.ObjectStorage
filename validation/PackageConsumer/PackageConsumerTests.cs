using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Audorix.ObjectStorage;
using Audorix.ObjectStorage.Api;
using Audorix.ObjectStorage.Api.DependencyInjection;
using Audorix.ObjectStorage.Api.Routing;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using AwsPutObjectRequest = Amazon.S3.Model.PutObjectRequest;

namespace PackageConsumer;

public sealed class PackageConsumerTests
{
    private const string AccessKey = "PACKAGECONSUMER";
    private const string SecretKey = "package-consumer-validation-secret";
    private const string Region = "us-east-1";
    private const string TokenKey = "package-consumer-continuation-token-signing-key";

    [Fact]
    public async Task LocalPackagesWorkThroughDirectS3AndAuthenticatedApiAcrossRestart()
    {
        string connectionString = ReadConnectionString();
        string schema = "audorix_package_consumer_" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "Audorix.ObjectStorage.PackageConsumer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using (ConsumerHost first = await ConsumerHost.StartAsync(connectionString, schema, root))
            {
                await first.Storage.CreateBucketAsync("integration");
                await first.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest(
                    "integration", "tenant-a/direct.txt", new MemoryStream("direct"u8.ToArray()), "text/plain"));
                ObjectListPage direct = await first.Storage.ListObjectsAsync("integration", "tenant-a/");
                Assert.Contains(direct.Objects, item => item.Key == "tenant-a/direct.txt");

                using AmazonS3Client s3 = CreateS3Client(first.Endpoint, AccessKey, SecretKey);
                byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.7\npackage consumer\n%%EOF");
                await s3.PutObjectAsync(new AwsPutObjectRequest
                {
                    BucketName = "integration",
                    Key = "tenant-a/sdk.pdf",
                    InputStream = new MemoryStream(pdf),
                    ContentType = "application/pdf",
                    UseChunkEncoding = false,
                    Metadata = { ["visible"] = "yes", ["private"] = "hidden" }
                });
                await s3.PutObjectAsync(new AwsPutObjectRequest
                {
                    BucketName = "integration",
                    Key = "tenant-a/delete.txt",
                    InputStream = new MemoryStream("delete"u8.ToArray()),
                    UseChunkEncoding = false
                });

                GetObjectMetadataResponse head = await s3.GetObjectMetadataAsync("integration", "tenant-a/sdk.pdf");
                Assert.Equal(pdf.Length, head.ContentLength);
                using (GetObjectResponse get = await s3.GetObjectAsync("integration", "tenant-a/sdk.pdf"))
                    Assert.Equal(pdf, await ReadAllAsync(get.ResponseStream));
                using (GetObjectResponse range = await s3.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = "integration",
                    Key = "tenant-a/sdk.pdf",
                    ByteRange = new ByteRange(0, 3)
                }))
                {
                    Assert.Equal(HttpStatusCode.PartialContent, range.HttpStatusCode);
                    Assert.Equal("%PDF", Encoding.ASCII.GetString(await ReadAllAsync(range.ResponseStream)));
                }
                ListObjectsV2Response listed = await s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = "integration",
                    Prefix = "tenant-a/"
                });
                Assert.Contains(listed.S3Objects, item => item.Key == "tenant-a/sdk.pdf");
                Assert.Equal(HttpStatusCode.NoContent, (await s3.DeleteObjectAsync("integration", "tenant-a/delete.txt")).HttpStatusCode);

                using AmazonS3Client invalid = CreateS3Client(first.Endpoint, "BADKEY", "bad-secret");
                AmazonS3Exception badCredential = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
                    () => invalid.GetObjectAsync("integration", "tenant-a/sdk.pdf"));
                Assert.Equal(HttpStatusCode.Forbidden, badCredential.StatusCode);

                Assert.Equal(HttpStatusCode.Unauthorized, (await first.Http.GetAsync("/files/objects?scope=docs")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await first.SendApiAsync(HttpMethod.Get, "/files/objects?scope=docs", "beta")).StatusCode);

                string metadataUrl = QueryHelpers.AddQueryString("/files/metadata", new Dictionary<string, string?>
                {
                    ["scope"] = "docs", ["key"] = "sdk.pdf"
                });
                ObjectStorageApiMetadataResponse metadata = (await (await first.SendApiAsync(HttpMethod.Get, metadataUrl, "alpha"))
                    .Content.ReadFromJsonAsync<ObjectStorageApiMetadataResponse>())!;
                Assert.Equal("yes", metadata.Metadata["visible"]);
                Assert.DoesNotContain("private", metadata.Metadata.Keys, StringComparer.OrdinalIgnoreCase);

                string contentUrl = QueryHelpers.AddQueryString("/files/view", new Dictionary<string, string?>
                {
                    ["scope"] = "docs", ["key"] = "sdk.pdf"
                });
                HttpResponseMessage view = await first.SendApiAsync(HttpMethod.Get, contentUrl, "alpha");
                Assert.Equal("inline", view.Content.Headers.ContentDisposition!.DispositionType);
                Assert.Equal(pdf, await view.Content.ReadAsByteArrayAsync());
                HttpResponseMessage apiHead = await first.SendApiAsync(HttpMethod.Head, contentUrl, "alpha");
                Assert.Equal(pdf.Length, apiHead.Content.Headers.ContentLength);
                HttpResponseMessage apiRange = await first.SendApiAsync(HttpMethod.Get, contentUrl, "alpha", request =>
                    request.Headers.Range = new RangeHeaderValue(0, 3));
                Assert.Equal(HttpStatusCode.PartialContent, apiRange.StatusCode);
                Assert.Equal("%PDF", await apiRange.Content.ReadAsStringAsync());
            }

            await using (ConsumerHost restarted = await ConsumerHost.StartAsync(connectionString, schema, root))
            {
                using AmazonS3Client s3 = CreateS3Client(restarted.Endpoint, AccessKey, SecretKey);
                using GetObjectResponse response = await s3.GetObjectAsync("integration", "tenant-a/sdk.pdf");
                Assert.Contains("package consumer", Encoding.ASCII.GetString(await ReadAllAsync(response.ResponseStream)));
                string browseUrl = "/files/objects?scope=docs";
                ObjectStorageApiBrowseResponse browse = (await (await restarted.SendApiAsync(HttpMethod.Get, browseUrl, "alpha"))
                    .Content.ReadFromJsonAsync<ObjectStorageApiBrowseResponse>())!;
                Assert.Contains(browse.Items, item => item.Key == "sdk.pdf");
                await s3.DeleteObjectAsync("integration", "tenant-a/sdk.pdf");
                Assert.Null(await restarted.Storage.GetObjectAsync("integration", "tenant-a/sdk.pdf"));
            }
        }
        finally
        {
            await DropSchemaAsync(connectionString, schema);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static AmazonS3Client CreateS3Client(string endpoint, string accessKey, string secretKey) =>
        new(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint + "/s3",
            AuthenticationRegion = Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 0
        });

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static string ReadConnectionString()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "Audorix.ObjectStorage.PostgreSql", "data", "database.json");
            if (File.Exists(candidate))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(candidate));
                return document.RootElement.GetProperty("Database").GetProperty("ActiveConnectionString").GetString()
                    ?? throw new InvalidOperationException("database.json has no active connection string.");
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("The local PostgreSQL database.json was not found.");
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        if (!schema.StartsWith("audorix_package_consumer_", StringComparison.Ordinal)) return;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"drop schema if exists \"{schema}\" cascade;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ConsumerHost : IAsyncDisposable
    {
        private readonly WebApplication app;

        private ConsumerHost(WebApplication app, string endpoint)
        {
            this.app = app;
            Endpoint = endpoint;
            Http = new HttpClient { BaseAddress = new Uri(endpoint) };
            Storage = app.Services.GetRequiredService<IObjectStorageEngine>();
        }

        public string Endpoint { get; }
        public HttpClient Http { get; }
        public IObjectStorageEngine Storage { get; }

        public static async Task<ConsumerHost> StartAsync(string connectionString, string schema, string root)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAuthentication("Consumer")
                .AddScheme<AuthenticationSchemeOptions, ConsumerAuthenticationHandler>("Consumer", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddAudorixObjectStorage(options => options.StorageRootPath = root);
            builder.Services.AddAudorixObjectStoragePostgreSql(options =>
            {
                options.ConnectionString = connectionString;
                options.SchemaName = schema;
            });
            builder.Services.AddAudorixObjectStorageS3(options =>
            {
                options.Region = Region;
                options.ContinuationTokenSigningKey = TokenKey;
                options.Credentials.Add(new S3Credential
                {
                    AccessKeyId = AccessKey,
                    SecretAccessKey = SecretKey,
                    AllowedBuckets = ["integration"],
                    AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
                });
            });
            builder.Services.AddAudorixObjectStorageApi(options => options.ExposedMetadataKeys.Add("visible"));
            builder.Services.AddSingleton<IObjectStorageApiAuthorization, ConsumerAuthorization>();
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "data-protection")));

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAudorixObjectStorageS3("/s3");
            app.MapAudorixObjectStorageApi("/files");
            await app.StartAsync();
            string endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new ConsumerHost(app, endpoint);
        }

        public async Task<HttpResponseMessage> SendApiAsync(
            HttpMethod method,
            string path,
            string user,
            Action<HttpRequestMessage>? configure = null)
        {
            HttpRequestMessage request = new(method, path);
            request.Headers.Add("X-Package-Consumer-User", user);
            configure?.Invoke(request);
            return await Http.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class ConsumerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Package-Consumer-User", out var user) || string.IsNullOrWhiteSpace(user))
                return Task.FromResult(AuthenticateResult.NoResult());
            ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class ConsumerAuthorization : IObjectStorageApiAuthorization
    {
        public ValueTask<ObjectStorageApiGrant?> AuthorizeAsync(
            ObjectStorageApiAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectStorageApiGrant? grant = request.HttpContext.User.Identity?.Name == "alpha"
                && request.LogicalScope == "docs"
                ? new("integration", "tenant-a/")
                : null;
            return ValueTask.FromResult(grant);
        }
    }
}
