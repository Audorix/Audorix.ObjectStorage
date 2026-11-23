using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Audorix.ObjectStorage.Api;
using Audorix.ObjectStorage.Api.DependencyInjection;
using Audorix.ObjectStorage.Api.Routing;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
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

namespace Audorix.ObjectStorage.Tests;

public sealed class ObjectStorageApiIntegrationTests
{
    [PostgreSqlFact]
    public async Task AuthenticationScopesBrowsingTokensAndMetadataAreProtected()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.PutAsync("tenant-a/folder/a %.txt", "alpha", "text/plain", new Dictionary<string, string>
        {
            ["visible"] = "yes",
            ["secret"] = "never"
        });
        await host.PutAsync("tenant-a/folder/sub/item.txt", "nested", "text/plain");
        await host.PutAsync("tenant-a/unicodé/space % + # ?.txt", "exact", "text/plain");
        await host.PutAsync("tenant-a/wild_%/literal.txt", "wildcard", "text/plain");
        await host.PutAsync("tenant-b/secret.txt", "beta", "text/plain");

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/files/objects?scope=docs")).StatusCode);
        foreach (string path in new[]
        {
            "/files/objects?scope=tenant-b",
            "/files/metadata?scope=tenant-b&key=secret.txt",
            "/files/download?scope=tenant-b&key=secret.txt",
            "/files/view?scope=tenant-b&key=secret.txt"
        })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, path, "alpha")).StatusCode);
        }

        string firstUrl = QueryHelpers.AddQueryString("/files/objects", new Dictionary<string, string?>
        {
            ["scope"] = "docs",
            ["prefix"] = "folder/",
            ["delimiter"] = "/",
            ["pageSize"] = "1"
        });
        HttpResponseMessage firstResponse = await host.SendAsync(HttpMethod.Get, firstUrl, "alpha");
        firstResponse.EnsureSuccessStatusCode();
        ObjectStorageApiBrowseResponse first = (await firstResponse.Content.ReadFromJsonAsync<ObjectStorageApiBrowseResponse>())!;
        Assert.Single(first.Items);
        Assert.Equal("folder/a %.txt", first.Items[0].Key);
        Assert.Equal("yes", first.Items[0].Metadata["visible"]);
        Assert.DoesNotContain("secret", first.Items[0].Metadata.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(first.ContinuationToken);

        string secondUrl = QueryHelpers.AddQueryString(firstUrl, "continuationToken", first.ContinuationToken);
        ObjectStorageApiBrowseResponse second = (await (await host.SendAsync(HttpMethod.Get, secondUrl, "alpha"))
            .Content.ReadFromJsonAsync<ObjectStorageApiBrowseResponse>())!;
        Assert.Single(second.Items);
        Assert.Equal("prefix", second.Items[0].Kind);
        Assert.Equal("folder/sub/", second.Items[0].Key);

        string wrongParameters = QueryHelpers.AddQueryString("/files/objects", new Dictionary<string, string?>
        {
            ["scope"] = "docs",
            ["prefix"] = "folder/",
            ["delimiter"] = "/",
            ["pageSize"] = "2",
            ["continuationToken"] = first.ContinuationToken
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Get, wrongParameters, "alpha")).StatusCode);
        string wrongScope = QueryHelpers.AddQueryString("/files/objects", new Dictionary<string, string?>
        {
            ["scope"] = "other",
            ["prefix"] = "folder/",
            ["delimiter"] = "/",
            ["pageSize"] = "1",
            ["continuationToken"] = first.ContinuationToken
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Get, wrongScope, "alpha")).StatusCode);

        string exactKey = "unicodé/space % + # ?.txt";
        string metadataUrl = QueryHelpers.AddQueryString("/files/metadata", new Dictionary<string, string?>
        {
            ["scope"] = "docs",
            ["key"] = exactKey
        });
        HttpResponseMessage metadataResponse = await host.SendAsync(HttpMethod.Get, metadataUrl, "alpha");
        metadataResponse.EnsureSuccessStatusCode();
        string metadataJson = await metadataResponse.Content.ReadAsStringAsync();
        ObjectStorageApiMetadataResponse metadata = JsonSerializer.Deserialize<ObjectStorageApiMetadataResponse>(metadataJson, JsonOptions)!;
        Assert.Equal(exactKey, metadata.Key);
        Assert.DoesNotContain("bucket", metadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blobId", metadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host.Root, metadataJson, StringComparison.OrdinalIgnoreCase);
        ObjectInfo stored = (await host.Storage.GetObjectAsync("integration", "tenant-a/" + exactKey))!;
        Assert.DoesNotContain(stored.BlobId, metadataJson, StringComparison.Ordinal);
        Assert.Equal("exact", await (await host.SendAsync(HttpMethod.Get,
            QueryHelpers.AddQueryString("/files/download", new Dictionary<string, string?> { ["scope"] = "docs", ["key"] = exactKey }),
            "alpha")).Content.ReadAsStringAsync());

        string wildcardUrl = QueryHelpers.AddQueryString("/files/objects", new Dictionary<string, string?>
        {
            ["scope"] = "docs",
            ["prefix"] = "wild_%/"
        });
        ObjectStorageApiBrowseResponse wildcardPage = (await (await host.SendAsync(HttpMethod.Get, wildcardUrl, "alpha"))
            .Content.ReadFromJsonAsync<ObjectStorageApiBrowseResponse>())!;
        Assert.Equal("wild_%/literal.txt", Assert.Single(wildcardPage.Items).Key);
    }

    [PostgreSqlFact]
    public async Task DownloadsViewsHeadAndRangesUseSafeHttpSemantics()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        byte[] pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nbody\n%%EOF");
        await host.PutAsync("tenant-a/reports/résumé.pdf", pdf, "application/pdf");
        await host.PutAsync("tenant-a/page.html", "<script>alert(1)</script>", "text/html");
        await host.PutAsync("tenant-a/vector.svg", "<svg/>", "image/svg+xml");
        await host.PutAsync("tenant-a/unknown.bin", "unknown", null);

        string pdfUrl = QueryHelpers.AddQueryString("/files/view", new Dictionary<string, string?>
        {
            ["scope"] = "docs",
            ["key"] = "reports/résumé.pdf"
        });
        HttpResponseMessage view = await host.SendAsync(HttpMethod.Get, pdfUrl, "alpha");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal("inline", view.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", view.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", view.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.Equal(pdf, await view.Content.ReadAsByteArrayAsync());

        string downloadUrl = pdfUrl.Replace("/view", "/download", StringComparison.Ordinal);
        HttpResponseMessage download = await host.SendAsync(HttpMethod.Get, downloadUrl, "alpha");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("résumé.pdf", download.Content.Headers.ContentDisposition.FileNameStar);

        HttpResponseMessage head = await host.SendAsync(HttpMethod.Head, downloadUrl, "alpha");
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(pdf.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes", head.Headers.AcceptRanges.Single());

        await AssertRangeAsync(host, downloadUrl, "bytes=0-3", "%PDF", 0, 3, pdf.Length);
        await AssertRangeAsync(host, downloadUrl, "bytes=5-", "1.7\nbody\n%%EOF", 5, pdf.Length - 1, pdf.Length);
        await AssertRangeAsync(host, downloadUrl, "bytes=-5", "%%EOF", pdf.Length - 5, pdf.Length - 1, pdf.Length);

        HttpResponseMessage rangedHead = await host.SendAsync(HttpMethod.Head, downloadUrl, "alpha", request => request.Headers.Range = new RangeHeaderValue(0, 3));
        Assert.Equal(HttpStatusCode.PartialContent, rangedHead.StatusCode);
        Assert.Equal(4, rangedHead.Content.Headers.ContentLength);
        Assert.Empty(await rangedHead.Content.ReadAsByteArrayAsync());

        HttpResponseMessage unsatisfiable = await host.SendAsync(HttpMethod.Get, downloadUrl, "alpha", request => request.Headers.Range = new RangeHeaderValue(pdf.Length, null));
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, unsatisfiable.StatusCode);
        Assert.Equal($"bytes */{pdf.Length}", unsatisfiable.Content.Headers.ContentRange!.ToString());

        foreach (string range in new[] { "bytes=4-1", "items=0-1", "bytes=0-1,4-5" })
        {
            HttpResponseMessage invalid = await host.SendAsync(HttpMethod.Get, downloadUrl, "alpha", request => request.Headers.TryAddWithoutValidation("Range", range));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }

        HttpResponseMessage conditional = await host.SendAsync(HttpMethod.Get, downloadUrl, "alpha", request => request.Headers.TryAddWithoutValidation("If-Range", "\"etag\""));
        Assert.Equal(HttpStatusCode.BadRequest, conditional.StatusCode);

        foreach (string key in new[] { "page.html", "vector.svg", "unknown.bin" })
        {
            string url = QueryHelpers.AddQueryString("/files/view", new Dictionary<string, string?> { ["scope"] = "docs", ["key"] = key });
            HttpResponseMessage response = await host.SendAsync(HttpMethod.Get, url, "alpha");
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        }

        string namedUrl = QueryHelpers.AddQueryString(downloadUrl, "filename", "factualité 2026.pdf");
        HttpResponseMessage named = await host.SendAsync(HttpMethod.Get, namedUrl, "alpha");
        Assert.Equal("factualité 2026.pdf", named.Content.Headers.ContentDisposition!.FileNameStar);
        string injectedUrl = downloadUrl + "&filename=bad%0d%0aX-Injected%3Ayes";
        HttpResponseMessage injected = await host.SendAsync(HttpMethod.Get, injectedUrl, "alpha");
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        Assert.False(injected.Headers.Contains("X-Injected"));
    }

    [PostgreSqlFact]
    public async Task DistinctPermissionsAndMissingObjectsAreHidden()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        await host.PutAsync("tenant-a/readable.txt", "content", "text/plain");
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/files/objects?scope=docs", "browser")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/files/metadata?scope=docs&key=readable.txt", "browser")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/files/download?scope=docs&key=readable.txt", "browser")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/files/view?scope=docs&key=readable.txt", "browser")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/files/metadata?scope=docs&key=missing.txt", "alpha")).StatusCode);
    }

    [PostgreSqlFact]
    public async Task ContinuationTokensExpireAndCurrentAuthorizationIsAlwaysRechecked()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync(options =>
            options.ContinuationTokenLifetime = TimeSpan.FromMilliseconds(50));
        await host.PutAsync("tenant-a/a", "a", "text/plain");
        await host.PutAsync("tenant-a/b", "b", "text/plain");

        ObjectStorageApiBrowseResponse first = (await (await host.SendAsync(
            HttpMethod.Get, "/files/objects?scope=docs&pageSize=1", "alpha"))
            .Content.ReadFromJsonAsync<ObjectStorageApiBrowseResponse>())!;
        Assert.NotNull(first.ContinuationToken);
        string next = QueryHelpers.AddQueryString("/files/objects", new Dictionary<string, string?>
        {
            ["scope"] = "docs",
            ["pageSize"] = "1",
            ["continuationToken"] = first.ContinuationToken
        });

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, next, "denied")).StatusCode);
        await Task.Delay(100);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Get, next, "alpha")).StatusCode);
    }

    [Fact]
    public async Task DefaultAuthorizationIntegrationDeniesAccess()
    {
        DefaultHttpContext context = new();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], "Test"));
        DenyAllObjectStorageApiAuthorization authorization = new();
        ObjectStorageApiGrant? grant = await authorization.AuthorizeAsync(
            new ObjectStorageApiAuthorizationRequest(context, ObjectStorageApiOperation.Browse, "docs"),
            CancellationToken.None);
        Assert.Null(grant);
    }

    [PostgreSqlFact]
    public async Task StreamingPreservesCommittedVersionAcrossOverwriteDeleteAndCancellation()
    {
        await using ApiTestHost host = await ApiTestHost.StartAsync();
        byte[] original = Enumerable.Repeat((byte)0x31, 32 * 1024 * 1024).ToArray();
        await host.PutAsync("tenant-a/large.bin", original, "application/octet-stream");
        string url = "/files/download?scope=docs&key=large.bin";

        using HttpRequestMessage request = host.CreateRequest(HttpMethod.Get, url, "alpha");
        using HttpResponseMessage response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using Stream active = await response.Content.ReadAsStreamAsync();
        byte[] first = new byte[4096];
        await active.ReadExactlyAsync(first);
        await host.PutAsync("tenant-a/large.bin", "replacement", "application/octet-stream");
        Assert.True(await host.Storage.DeleteObjectAsync("integration", "tenant-a/large.bin"));
        await host.Storage.CleanupUnreferencedBlobsAsync();

        using MemoryStream received = new();
        await received.WriteAsync(first);
        await active.CopyToAsync(received);
        Assert.Equal(original, received.ToArray());

        await host.PutAsync("tenant-a/cancel.bin", original, "application/octet-stream");
        using CancellationTokenSource cancellation = new();
        using HttpRequestMessage cancellationRequest = host.CreateRequest(HttpMethod.Get, "/files/download?scope=docs&key=cancel.bin", "alpha");
        using HttpResponseMessage cancellationResponse = await host.Client.SendAsync(cancellationRequest, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await using Stream cancellationStream = await cancellationResponse.Content.ReadAsStreamAsync(cancellation.Token);
        int bytesRead = await cancellationStream.ReadAsync(new byte[4096], cancellation.Token);
        Assert.True(bytesRead > 0);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellationStream.CopyToAsync(Stream.Null, cancellation.Token));
    }

    private static async Task AssertRangeAsync(ApiTestHost host, string url, string range, string expected, int start, int end, int fullLength)
    {
        HttpResponseMessage response = await host.SendAsync(HttpMethod.Get, url, "alpha", request => request.Headers.TryAddWithoutValidation("Range", range));
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal($"bytes {start}-{end}/{fullLength}", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user) || string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            ClaimsPrincipal principal = new(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, user.ToString())],
                Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class TestAuthorization : IObjectStorageApiAuthorization
    {
        public ValueTask<ObjectStorageApiGrant?> AuthorizeAsync(ObjectStorageApiAuthorizationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? user = request.HttpContext.User.Identity?.Name;
            ObjectStorageApiGrant? grant = (user, request.LogicalScope, request.Operation) switch
            {
                ("alpha", "docs", _) => new("integration", "tenant-a/"),
                ("alpha", "other", _) => new("integration", "tenant-a-other/"),
                ("beta", "docs", _) => new("integration", "tenant-b/"),
                ("browser", "docs", ObjectStorageApiOperation.Browse or ObjectStorageApiOperation.Metadata) => new("integration", "tenant-a/"),
                _ => null
            };
            return ValueTask.FromResult(grant);
        }
    }

    private sealed class ApiTestHost : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _connectionString;
        private readonly string _schema;

        private ApiTestHost(WebApplication application, HttpClient client, string root, string connectionString, string schema)
        {
            _application = application;
            Client = client;
            Root = root;
            _connectionString = connectionString;
            _schema = schema;
            Storage = application.Services.GetRequiredService<IObjectStorageEngine>();
        }

        public HttpClient Client { get; }
        public string Root { get; }
        public IObjectStorageEngine Storage { get; }

        public static async Task<ApiTestHost> StartAsync(Action<ObjectStorageApiOptions>? configureApi = null)
        {
            string connectionString = GetPostgreSqlConnectionString()
                ?? throw new InvalidOperationException("PostgreSQL test connection is not configured.");
            string schema = "audorix_api_test_" + Guid.NewGuid().ToString("N");
            string root = Path.Combine(Path.GetTempPath(), "Audorix.ObjectStorage.Api.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddAudorixObjectStorage(options =>
            {
                options.StorageRootPath = root;
                options.UnreferencedBlobRetention = TimeSpan.Zero;
            });
            builder.Services.AddAudorixObjectStoragePostgreSql(options =>
            {
                options.ConnectionString = connectionString;
                options.SchemaName = schema;
            });
            builder.Services.AddAudorixObjectStorageApi(options =>
            {
                options.ExposedMetadataKeys.Add("visible");
                configureApi?.Invoke(options);
            });
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "data-protection")));
            builder.Services.AddSingleton<IObjectStorageApiAuthorization, TestAuthorization>();

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapAudorixObjectStorageApi("/files");
            await app.StartAsync();
            string endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            HttpClient client = new() { BaseAddress = new Uri(endpoint) };
            ApiTestHost host = new(app, client, root, connectionString, schema);
            await host.Storage.CreateBucketAsync("integration");
            return host;
        }

        public HttpRequestMessage CreateRequest(HttpMethod method, string path, string? user)
        {
            HttpRequestMessage request = new(method, path);
            if (user is not null)
            {
                request.Headers.Add("X-Test-User", user);
            }

            return request;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? user, Action<HttpRequestMessage>? configure = null)
        {
            HttpRequestMessage request = CreateRequest(method, path, user);
            configure?.Invoke(request);
            return await Client.SendAsync(request);
        }

        public Task<ObjectInfo> PutAsync(string key, string content, string? contentType, IReadOnlyDictionary<string, string>? metadata = null) =>
            PutAsync(key, Encoding.UTF8.GetBytes(content), contentType, metadata);

        public async Task<ObjectInfo> PutAsync(string key, byte[] content, string? contentType, IReadOnlyDictionary<string, string>? metadata = null) =>
            await Storage.PutObjectAsync(new PutObjectRequest("integration", key, new MemoryStream(content), contentType, metadata));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
            await using NpgsqlConnection connection = new(_connectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new($"drop schema if exists \"{_schema}\" cascade;", connection);
            await command.ExecuteNonQueryAsync();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? GetPostgreSqlConnectionString()
    {
        string? configured = Environment.GetEnvironmentVariable("AUDORIX_OBJECTSTORAGE_POSTGRES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "Audorix.ObjectStorage.PostgreSql", "data", "database.json"));
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("Database").GetProperty("ActiveConnectionString").GetString();
    }

    private sealed class PostgreSqlFactAttribute : FactAttribute
    {
        public PostgreSqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(GetPostgreSqlConnectionString()))
            {
                Skip = "Configure database.json or AUDORIX_OBJECTSTORAGE_POSTGRES to execute this test.";
            }
        }
    }
}
