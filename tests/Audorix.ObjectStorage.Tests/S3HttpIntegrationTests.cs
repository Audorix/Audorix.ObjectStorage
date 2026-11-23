using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.Metadata;
using Audorix.ObjectStorage.PostgreSql;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using AwsPutObjectRequest = Amazon.S3.Model.PutObjectRequest;

namespace Audorix.ObjectStorage.Tests;

public sealed class S3HttpIntegrationTests
{
    private const string AccessKey = "AUDORIXINTEGRATION";
    private const string SecretKey = "audorix-integration-secret-key";
    private const string ReadOnlyAccessKey = "AUDORIXREADONLY";
    private const string ReadOnlySecretKey = "audorix-read-only-secret-key";
    private const string Region = "us-east-1";
    private const string TokenSigningKey = "audorix-integration-continuation-token-key-v1";
    private const string ListAccessKey = "AUDORIXLISTONLY";
    private const string ListSecretKey = "audorix-list-only-secret-key";

    [Fact]
    public async Task AwsSdkPutGetHeadDeleteWorksOverRealKestrelListener()
    {
        await using S3TestHost host = await S3TestHost.StartAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);

        string key = "nested/unicodé/space % #?.txt";
        byte[] expected = "S3 round trip"u8.ToArray();
        PutObjectResponse put = await client.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = "integration",
            Key = key,
            InputStream = new MemoryStream(expected),
            ContentType = "text/plain",
            UseChunkEncoding = false,
            Metadata = { ["owner"] = "sdk" }
        });

        Assert.Equal(HttpStatusCode.OK, put.HttpStatusCode);
        Assert.Matches("^\"[0-9a-f]{32}-1\"$", put.ETag);

        GetObjectMetadataResponse head = await client.GetObjectMetadataAsync("integration", key);
        Assert.Equal(expected.Length, head.ContentLength);
        Assert.Equal("text/plain", head.Headers.ContentType);
        Assert.Equal("sdk", head.Metadata["owner"]);
        Assert.Equal(put.ETag, head.ETag);

        using GetObjectResponse get = await client.GetObjectAsync("integration", key);
        using MemoryStream copy = new();
        await get.ResponseStream.CopyToAsync(copy);
        Assert.Equal(expected, copy.ToArray());

        DeleteObjectResponse delete = await client.DeleteObjectAsync("integration", key);
        Assert.Equal(HttpStatusCode.NoContent, delete.HttpStatusCode);
        AmazonS3Exception missing = await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => client.GetObjectAsync("integration", key));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [PostgreSqlFact]
    public async Task SingleRangeGetWorksThroughAwsSdkAndRealPostgreSql()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);
        byte[] content = Enumerable.Range(0, 512).Select(x => (byte)(x % 251)).ToArray();
        const string key = "ranges/space % & unicodé.bin";
        await client.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = "integration",
            Key = key,
            InputStream = new MemoryStream(content),
            UseChunkEncoding = false
        });

        using GetObjectResponse explicitRange = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "integration", Key = key, ByteRange = new ByteRange(0, 99)
        });
        Assert.Equal(HttpStatusCode.PartialContent, explicitRange.HttpStatusCode);
        Assert.Equal("bytes 0-99/512", explicitRange.ContentRange);
        Assert.Equal(content[..100], await ReadBytesAsync(explicitRange.ResponseStream));

        using GetObjectResponse openEnded = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "integration", Key = key, ByteRange = new ByteRange("bytes=500-")
        });
        Assert.Equal("bytes 500-511/512", openEnded.ContentRange);
        Assert.Equal(content[500..], await ReadBytesAsync(openEnded.ResponseStream));

        using GetObjectResponse suffix = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "integration", Key = key, ByteRange = new ByteRange("bytes=-17")
        });
        Assert.Equal("bytes 495-511/512", suffix.ContentRange);
        Assert.Equal(content[^17..], await ReadBytesAsync(suffix.ResponseStream));

        using GetObjectResponse lastByte = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "integration", Key = key, ByteRange = new ByteRange(511, 999)
        });
        Assert.Equal(content[^1..], await ReadBytesAsync(lastByte.ResponseStream));

        GetObjectMetadataResponse head = await client.GetObjectMetadataAsync("integration", key);
        Assert.Equal(512, head.ContentLength);
        Assert.Equal(HttpStatusCode.OK, head.HttpStatusCode);
        using GetObjectResponse full = await client.GetObjectAsync("integration", key);
        Assert.Equal(content, await ReadBytesAsync(full.ResponseStream));
    }

    [PostgreSqlFact]
    public async Task RangeErrorsAuthorizationAndLargeObjectTransferAreBounded()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);
        await client.PutObjectAsync(SdkPut("integration", "range-errors", "0123456789"));
        await client.PutObjectAsync(SdkPut("integration", "empty-range", ""));

        using HttpClient http = new();
        foreach ((string value, HttpStatusCode status) in new[]
        {
            ("bytes=abc-def", HttpStatusCode.BadRequest),
            ("items=0-1", HttpStatusCode.BadRequest),
            ("bytes=8-2", HttpStatusCode.BadRequest),
            ("bytes=0-1,4-5", HttpStatusCode.NotImplemented)
        })
        {
            using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Get, host.Endpoint + "/s3/integration/range-errors", AccessKey, SecretKey, [], DateTimeOffset.UtcNow);
            request.Headers.TryAddWithoutValidation("Range", value);
            using HttpResponseMessage response = await http.SendAsync(request);
            Assert.Equal(status, response.StatusCode);
        }

        using (HttpRequestMessage ifRange = CreateSignedRequest(HttpMethod.Get, host.Endpoint + "/s3/integration/range-errors", AccessKey, SecretKey, [], DateTimeOffset.UtcNow))
        {
            ifRange.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
            ifRange.Headers.TryAddWithoutValidation("If-Range", "\"validator\"");
            using HttpResponseMessage response = await http.SendAsync(ifRange);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        }
        using (HttpRequestMessage rangedHead = CreateSignedRequest(HttpMethod.Head, host.Endpoint + "/s3/integration/range-errors", AccessKey, SecretKey, [], DateTimeOffset.UtcNow))
        {
            rangedHead.Headers.TryAddWithoutValidation("Range", "bytes=0-1");
            using HttpResponseMessage response = await http.SendAsync(rangedHead);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        }

        foreach ((string key, string range, long length) in new[]
        {
            ("range-errors", "bytes=10-", 10L),
            ("range-errors", "bytes=-0", 10L),
            ("empty-range", "bytes=0-0", 0L)
        })
        {
            using HttpRequestMessage request = CreateSignedRequest(HttpMethod.Get, host.Endpoint + "/s3/integration/" + key, AccessKey, SecretKey, [], DateTimeOffset.UtcNow);
            request.Headers.TryAddWithoutValidation("Range", range);
            using HttpResponseMessage response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
            Assert.Equal($"bytes */{length}", response.Content.Headers.ContentRange?.ToString());
        }

        using AmazonS3Client denied = CreateClient(host.Endpoint, "UNKNOWNRANGEKEY", "unknown-range-secret");
        AmazonS3Exception forbidden = await Assert.ThrowsAsync<AmazonS3Exception>(() => denied.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "integration", Key = "range-errors", ByteRange = new ByteRange(0, 0)
        }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        const int largeLength = 32 * 1024 * 1024;
        string path = Path.Combine(host.Root, "large-range-source.bin");
        await using (FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            output.SetLength(largeLength);
            output.Position = largeLength - 4096;
            await output.WriteAsync(Enumerable.Range(0, 4096).Select(x => (byte)(x % 251)).ToArray());
        }
        await using (FileStream input = File.OpenRead(path))
        {
            await client.PutObjectAsync(new AwsPutObjectRequest { BucketName = "integration", Key = "large-range", InputStream = input, UseChunkEncoding = false });
        }
        using GetObjectResponse smallRange = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "integration", Key = "large-range", ByteRange = new ByteRange(largeLength - 4096, largeLength - 1)
        });
        byte[] transferred = await ReadBytesAsync(smallRange.ResponseStream);
        Assert.Equal(4096, smallRange.ContentLength);
        Assert.Equal(4096, transferred.Length);
        Assert.Equal($"bytes {largeLength - 4096}-{largeLength - 1}/{largeLength}", smallRange.ContentRange);
    }

    [Fact]
    public async Task DirectAndS3OperationsShareTheSameObjects()
    {
        await using S3TestHost host = await S3TestHost.StartAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);

        await host.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest(
            "integration",
            "direct-to-s3",
            new MemoryStream("direct"u8.ToArray()),
            "text/plain",
            new Dictionary<string, string> { ["source"] = "direct" }));

        using GetObjectResponse viaS3 = await client.GetObjectAsync("integration", "direct-to-s3");
        using StreamReader reader = new(viaS3.ResponseStream, Encoding.UTF8);
        Assert.Equal("direct", await reader.ReadToEndAsync());
        Assert.Equal("direct", viaS3.Metadata["source"]);

        await client.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = "integration",
            Key = "s3-to-direct",
            InputStream = new MemoryStream("s3"u8.ToArray()),
            UseChunkEncoding = false
        });
        await using ObjectReadResult viaDirect = await host.Storage.OpenReadAsync("integration", "s3-to-direct");
        using StreamReader directReader = new(viaDirect.Content, Encoding.UTF8);
        Assert.Equal("s3", await directReader.ReadToEndAsync());
    }

    [Fact]
    public async Task EmptyAndLargeObjectsRoundTripWithoutServerBuffering()
    {
        await using S3TestHost host = await S3TestHost.StartAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);

        await client.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = "integration",
            Key = "empty",
            InputStream = new MemoryStream(),
            UseChunkEncoding = false
        });
        using GetObjectResponse empty = await client.GetObjectAsync("integration", "empty");
        Assert.Equal(0, empty.ContentLength);

        string file = Path.Combine(host.Root, "large-source.bin");
        byte[] block = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i % 251)).ToArray();
        await using (FileStream output = File.Create(file))
        {
            for (int i = 0; i < 128; i++)
            {
                await output.WriteAsync(block);
            }
        }

        byte[] expectedHash = await HashFileAsync(file);
        await using (FileStream input = File.OpenRead(file))
        {
            await client.PutObjectAsync(new AwsPutObjectRequest
            {
                BucketName = "integration",
                Key = "large",
                InputStream = input,
                UseChunkEncoding = false
            });
        }

        using GetObjectResponse large = await client.GetObjectAsync("integration", "large");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await large.ResponseStream.ReadAsync(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        Assert.Equal(expectedHash, hash.GetHashAndReset());
        Assert.Equal(8L * 1024 * 1024, large.ContentLength);
    }

    [Fact]
    public async Task AuthenticationAndAuthorizationFailuresOccurBeforeMutation()
    {
        await using S3TestHost host = await S3TestHost.StartAsync();
        await host.Storage.CreateBucketAsync("integration");
        await host.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest("integration", "protected", new MemoryStream("old"u8.ToArray())));

        using HttpClient http = new();
        HttpResponseMessage unsigned = await http.GetAsync(host.Endpoint + "/s3/integration/protected");
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);

        using AmazonS3Client wrongSecret = CreateClient(host.Endpoint, AccessKey, "wrong-secret");
        AmazonS3Exception invalidSignature = await Assert.ThrowsAnyAsync<AmazonS3Exception>(
            () => wrongSecret.GetObjectAsync("integration", "protected"));
        Assert.Equal(HttpStatusCode.Forbidden, invalidSignature.StatusCode);

        using HttpRequestMessage stale = CreateSignedRequest(
            HttpMethod.Get,
            host.Endpoint + "/s3/integration/protected",
            AccessKey,
            SecretKey,
            [],
            DateTimeOffset.UtcNow.AddHours(-1));
        HttpResponseMessage staleResponse = await http.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Forbidden, staleResponse.StatusCode);

        using AmazonS3Client readOnly = CreateClient(host.Endpoint, ReadOnlyAccessKey, ReadOnlySecretKey);
        AmazonS3Exception denied = await Assert.ThrowsAsync<AmazonS3Exception>(() => readOnly.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = "integration",
            Key = "denied",
            InputStream = new MemoryStream("no"u8.ToArray()),
            UseChunkEncoding = false
        }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(await host.Storage.GetObjectAsync("integration", "denied"));

        byte[] claimed = "claimed"u8.ToArray();
        using HttpRequestMessage tampered = CreateSignedRequest(
            HttpMethod.Put,
            host.Endpoint + "/s3/integration/protected",
            AccessKey,
            SecretKey,
            claimed,
            DateTimeOffset.UtcNow);
        tampered.Content = new ByteArrayContent("tampered"u8.ToArray());
        HttpResponseMessage tamperedResponse = await http.SendAsync(tampered);
        Assert.Equal(HttpStatusCode.BadRequest, tamperedResponse.StatusCode);

        using HttpRequestMessage unsignedMetadata = CreateSignedRequest(
            HttpMethod.Put,
            host.Endpoint + "/s3/integration/unsigned-metadata",
            AccessKey,
            SecretKey,
            "body"u8.ToArray(),
            DateTimeOffset.UtcNow);
        unsignedMetadata.Headers.TryAddWithoutValidation("x-amz-meta-owner", "tampered");
        using HttpResponseMessage unsignedMetadataResponse = await http.SendAsync(unsignedMetadata);
        Assert.Equal(HttpStatusCode.BadRequest, unsignedMetadataResponse.StatusCode);
        Assert.Null(await host.Storage.GetObjectAsync("integration", "unsigned-metadata"));

        await using ObjectReadResult preserved = await host.Storage.OpenReadAsync("integration", "protected");
        using StreamReader preservedReader = new(preserved.Content, Encoding.UTF8);
        Assert.Equal("old", await preservedReader.ReadToEndAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(host.Root, "staging"), "*.staging"));
    }

    [Fact]
    public async Task ContinuationTokensExpireAndAreBoundToTheIssuingCredential()
    {
        await using S3TestHost host = await S3TestHost.StartAsync(configureS3: options =>
            options.ContinuationTokenLifetime = TimeSpan.FromMilliseconds(50));
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client owner = CreateClient(host.Endpoint);
        await PutKeysAsync(owner, ["a", "b"]);

        ListObjectsV2Response first = await owner.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            MaxKeys = 1
        });
        using AmazonS3Client listOnly = CreateClient(host.Endpoint, ListAccessKey, ListSecretKey);
        await AssertInvalidTokenAsync(listOnly, "integration", first.NextContinuationToken);
        await Task.Delay(100);
        await AssertInvalidTokenAsync(owner, "integration", first.NextContinuationToken);
    }

    [Fact]
    public async Task CancelledHttpOverwritePreservesPreviouslyCommittedObject()
    {
        await using S3TestHost host = await S3TestHost.StartAsync();
        await host.Storage.CreateBucketAsync("integration");
        await host.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest("integration", "cancelled", new MemoryStream("old"u8.ToArray())));

        byte[] replacement = new byte[2 * 1024 * 1024];
        RandomNumberGenerator.Fill(replacement);
        using HttpRequestMessage request = CreateSignedRequest(
            HttpMethod.Put,
            host.Endpoint + "/s3/integration/cancelled",
            AccessKey,
            SecretKey,
            replacement,
            DateTimeOffset.UtcNow);
        request.Content = new SlowContent(replacement);
        using HttpClient http = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(75));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.SendAsync(request, cancellation.Token));
        await Task.Delay(100);

        await using ObjectReadResult preserved = await host.Storage.OpenReadAsync("integration", "cancelled");
        using StreamReader reader = new(preserved.Content, Encoding.UTF8);
        Assert.Equal("old", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task AdmissionRejectsExcessWorkAndCancellationReleasesPermitAndStagingFile()
    {
        await using S3TestHost host = await S3TestHost.StartAsync(configureS3: options =>
        {
            options.MaxConcurrentOperations = 1;
            options.MaxQueuedOperations = 0;
        });
        await host.Storage.CreateBucketAsync("integration");
        await host.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest(
            "integration", "existing", new MemoryStream("ready"u8.ToArray())));

        byte[] body = new byte[2 * 1024 * 1024];
        using HttpRequestMessage slow = CreateSignedRequest(
            HttpMethod.Put, host.Endpoint + "/s3/integration/slow", AccessKey, SecretKey, body, DateTimeOffset.UtcNow);
        slow.Content = new SlowContent(body);
        using HttpClient http = new();
        using CancellationTokenSource cancellation = new();
        Task<HttpResponseMessage> active = http.SendAsync(slow, cancellation.Token);
        await Task.Delay(100);

        using HttpRequestMessage excess = CreateSignedRequest(
            HttpMethod.Get, host.Endpoint + "/s3/integration/existing", AccessKey, SecretKey, [], DateTimeOffset.UtcNow);
        using HttpResponseMessage rejected = await http.SendAsync(excess);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await active);
        await Task.Delay(100);
        using HttpRequestMessage recovered = CreateSignedRequest(
            HttpMethod.Get, host.Endpoint + "/s3/integration/existing", AccessKey, SecretKey, [], DateTimeOffset.UtcNow);
        using HttpResponseMessage ok = await http.SendAsync(recovered);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(host.Root, "staging"), "*.staging"));
    }

    [AwsCliPostgreSqlFact]
    public async Task AwsCliCompatibilityProfileWorksWithRealPostgreSql()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        string source = Path.Combine(host.Root, "cli-source.bin");
        string destination = Path.Combine(host.Root, "cli-destination.bin");
        string rangeDestination = Path.Combine(host.Root, "cli-range.bin");
        string emptySource = Path.Combine(host.Root, "cli-empty.bin");
        byte[] expected = "aws cli round trip"u8.ToArray();
        await File.WriteAllBytesAsync(source, expected);
        await File.WriteAllBytesAsync(emptySource, []);
        Dictionary<string, string> defaultEnvironment = new()
        {
            ["AWS_ACCESS_KEY_ID"] = AccessKey,
            ["AWS_SECRET_ACCESS_KEY"] = SecretKey,
            ["AWS_DEFAULT_REGION"] = Region,
            ["AWS_EC2_METADATA_DISABLED"] = "true",
            ["AWS_CLI_AUTO_PROMPT"] = "off",
            ["AWS_PAGER"] = string.Empty,
            ["AWS_CONFIG_FILE"] = Path.Combine(host.Root, "isolated-aws-config"),
            ["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(host.Root, "isolated-aws-credentials")
        };
        Dictionary<string, string> compatibleEnvironment = new(defaultEnvironment)
        {
            ["AWS_REQUEST_CHECKSUM_CALCULATION"] = "WHEN_REQUIRED",
            ["AWS_RESPONSE_CHECKSUM_VALIDATION"] = "WHEN_REQUIRED"
        };

        string endpoint = host.Endpoint + "/s3";
        string aws = ResolveAwsCliPath()!;
        CommandResult defaultPut = await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "put-object", "--bucket", "integration", "--key", "default-probe", "--body", source],
            defaultEnvironment);
        Assert.True(
            defaultPut.Succeeded || defaultPut.CombinedOutput.Contains("NotImplemented", StringComparison.OrdinalIgnoreCase),
            "The default AWS CLI request failed for an unexpected reason: " + defaultPut.CombinedOutput);

        string specialKey = "nested/space % & <.txt";
        AssertCommandSucceeded(await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "put-object", "--bucket", "integration", "--key", specialKey, "--body", source, "--content-type", "text/plain", "--metadata", "owner=cli"],
            compatibleEnvironment));
        CommandResult head = await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "head-object", "--bucket", "integration", "--key", specialKey, "--output", "json"],
            compatibleEnvironment);
        AssertCommandSucceeded(head);
        Assert.Contains("text/plain", head.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("owner", head.StandardOutput, StringComparison.OrdinalIgnoreCase);

        AssertCommandSucceeded(await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "get-object", "--bucket", "integration", "--key", specialKey, destination],
            compatibleEnvironment));
        Assert.Equal(expected, await File.ReadAllBytesAsync(destination));
        AssertCommandSucceeded(await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "get-object", "--bucket", "integration", "--key", specialKey, "--range", "bytes=0-2", rangeDestination],
            compatibleEnvironment));
        Assert.Equal(expected[..3], await File.ReadAllBytesAsync(rangeDestination));
        AssertCommandSucceeded(await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "put-object", "--bucket", "integration", "--key", "empty", "--body", emptySource],
            compatibleEnvironment));

        CommandResult list = await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "list-objects-v2", "--bucket", "integration", "--output", "json"],
            compatibleEnvironment);
        AssertCommandSucceeded(list);
        using (JsonDocument listing = JsonDocument.Parse(list.StandardOutput))
        {
            string[] listedKeys = listing.RootElement.GetProperty("Contents")
                .EnumerateArray()
                .Select(item => Uri.UnescapeDataString(item.GetProperty("Key").GetString()!))
                .ToArray();
            Assert.Contains(specialKey, listedKeys);
            Assert.Contains("empty", listedKeys);
        }

        CommandResult highLevelList = await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3", "ls", "s3://integration", "--recursive"],
            compatibleEnvironment);
        AssertCommandSucceeded(highLevelList);
        Assert.Contains(specialKey, highLevelList.StandardOutput, StringComparison.Ordinal);

        AssertCommandSucceeded(await RunCommandAsync(aws,
            ["--endpoint-url", endpoint, "s3api", "delete-object", "--bucket", "integration", "--key", specialKey],
            compatibleEnvironment));
    }

    [PostgreSqlFact]
    public async Task AwsSdkRoundTripUsesRealPostgreSqlWhenConfigured()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);
        await client.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = "integration",
            Key = "postgresql",
            InputStream = new MemoryStream("database"u8.ToArray()),
            UseChunkEncoding = false
        });

        using GetObjectResponse response = await client.GetObjectAsync("integration", "postgresql");
        using StreamReader reader = new(response.ResponseStream, Encoding.UTF8);
        Assert.Equal("database", await reader.ReadToEndAsync());
    }

    [PostgreSqlFact]
    public async Task PostgreSqlBackedListObjectsV2SupportsPrefixesDelimitersAndPagination()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);
        string[] keys =
        [
            "alpha", "alpha/one", "alpha/two", "folder/item", "folder/sub/item",
            "percent%_literal", "percentXA", "space & <.txt", "éclair", "中"
        ];
        await PutKeysAsync(client, keys);

        ListObjectsV2Response empty = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            Prefix = "missing/"
        });
        Assert.True(empty.S3Objects is null or { Count: 0 });
        Assert.True(empty.CommonPrefixes is null or { Count: 0 });

        ListObjectsV2Response defaultLimit = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration"
        });
        Assert.Equal(1000, defaultLimit.MaxKeys);
        Assert.False(defaultLimit.IsTruncated);

        ListObjectsV2Response zeroLimit = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            MaxKeys = 0
        });
        Assert.Equal(0, zeroLimit.KeyCount);
        Assert.False(zeroLimit.IsTruncated);

        ListObjectsV2Response hierarchy = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            Delimiter = "/",
            Prefix = "folder/"
        });
        Assert.Equal(["folder/item"], hierarchy.S3Objects.Select(item => item.Key));
        Assert.Equal(["folder/sub/"], hierarchy.CommonPrefixes);

        ListObjectsV2Response literalWildcard = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            Prefix = "percent%_"
        });
        Assert.Equal(["percent%_literal"], literalWildcard.S3Objects.Select(item => item.Key));

        List<string> entries = [];
        string? token = null;
        do
        {
            ListObjectsV2Response page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = "integration",
                Delimiter = "/",
                MaxKeys = 2,
                ContinuationToken = token
            });
            int objectCount = page.S3Objects?.Count ?? 0;
            int prefixCount = page.CommonPrefixes?.Count ?? 0;
            Assert.InRange(objectCount + prefixCount, 1, 2);
            entries.AddRange(page.S3Objects?.Select(item => item.Key) ?? []);
            entries.AddRange(page.CommonPrefixes ?? []);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        Assert.Equal(entries.Count, entries.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ["alpha", "alpha/", "folder/", "percent%_literal", "percentXA", "space & <.txt", "éclair", "中"],
            entries.OrderBy(value => value, Utf8StringComparer.Instance));

        ListObjectsV2Response after = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            StartAfter = "percent%_literal",
            MaxKeys = 1
        });
        Assert.Equal(["percentXA"], after.S3Objects.Select(item => item.Key));

        ListObjectsV2Response encoded = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            Prefix = "space",
            Encoding = EncodingType.Url
        });
        Assert.Single(encoded.S3Objects);
        Assert.Equal("space & <.txt", Uri.UnescapeDataString(encoded.S3Objects[0].Key));
    }

    [PostgreSqlFact]
    public async Task ListObjectsV2RejectsUnauthorizedAndInvalidContinuationTokens()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        await host.Storage.CreateBucketAsync("other-bucket");
        host.AllowBucket("other-bucket");
        using AmazonS3Client client = CreateClient(host.Endpoint);
        await PutKeysAsync(client, ["a", "b", "c"]);

        ListObjectsV2Response first = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            MaxKeys = 1
        });
        Assert.True(first.IsTruncated);
        string token = first.NextContinuationToken;
        string tampered = token[..^1] + (token[^1] == 'A' ? "B" : "A");

        await AssertInvalidTokenAsync(client, "integration", tampered);
        await AssertInvalidTokenAsync(client, "integration", token, prefix: "changed");
        await AssertInvalidTokenAsync(client, "other-bucket", token);
        await AssertInvalidTokenAsync(client, "integration", "not-a-token");

        using AmazonS3Client readOnly = CreateClient(host.Endpoint, ReadOnlyAccessKey, ReadOnlySecretKey);
        AmazonS3Exception denied = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            readOnly.ListObjectsV2Async(new ListObjectsV2Request { BucketName = "integration" }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("AccessDenied", denied.ErrorCode);
    }

    [PostgreSqlFact]
    public async Task ListObjectsV2ContinuationUsesAForwardOnlyCursorDuringMutation()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);
        await PutKeysAsync(client, ["a", "c"]);

        ListObjectsV2Response first = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            MaxKeys = 1
        });
        Assert.Equal(["a"], first.S3Objects.Select(item => item.Key));

        await PutKeysAsync(client, ["0", "b"]);
        await client.DeleteObjectAsync("integration", "c");
        ListObjectsV2Response second = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "integration",
            MaxKeys = 10,
            ContinuationToken = first.NextContinuationToken
        });
        Assert.Equal(["b"], second.S3Objects.Select(item => item.Key));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlBackedOverwriteCancellationAndConcurrencyRemainConsistent()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);

        await client.PutObjectAsync(SdkPut("integration", "overwrite", "old"));
        await client.PutObjectAsync(SdkPut("integration", "overwrite", "new"));
        Assert.Equal("new", await ReadStringAsync(client, "integration", "overwrite"));

        byte[] replacement = new byte[2 * 1024 * 1024];
        RandomNumberGenerator.Fill(replacement);
        using HttpRequestMessage cancelledRequest = CreateSignedRequest(
            HttpMethod.Put,
            host.Endpoint + "/s3/integration/overwrite",
            AccessKey,
            SecretKey,
            replacement,
            DateTimeOffset.UtcNow);
        cancelledRequest.Content = new SlowContent(replacement);
        using HttpClient http = new();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(75));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.SendAsync(cancelledRequest, cancellation.Token));
        await Task.Delay(100);
        Assert.Equal("new", await ReadStringAsync(client, "integration", "overwrite"));

        string[] values = Enumerable.Range(0, 12).Select(index => "value-" + index).ToArray();
        await Task.WhenAll(values.Select(value => client.PutObjectAsync(SdkPut("integration", "concurrent", value))));
        Assert.Contains(await ReadStringAsync(client, "integration", "concurrent"), values);
    }

    [PostgreSqlFact]
    public async Task PostgreSqlBackedObjectSurvivesHostRestart()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using (S3TestHost first = await scope.StartHostAsync())
        {
            await first.Storage.CreateBucketAsync("integration");
            using AmazonS3Client client = CreateClient(first.Endpoint);
            await client.PutObjectAsync(SdkPut("integration", "restart", "persisted"));
        }

        await using S3TestHost second = await scope.StartHostAsync();
        using AmazonS3Client reopened = CreateClient(second.Endpoint);
        Assert.Equal("persisted", await ReadStringAsync(reopened, "integration", "restart"));
    }

    [PostgreSqlFact]
    public async Task PostgreSqlAndBlobBackupRestoreAsAConsistentRecoverySet()
    {
        string pgDump = @"C:\Program Files\PostgreSQL\18\bin\pg_dump.exe";
        string pgRestore = @"C:\Program Files\PostgreSQL\18\bin\pg_restore.exe";
        Assert.True(File.Exists(pgDump), "PostgreSQL pg_dump 18 is required for this integration test.");
        Assert.True(File.Exists(pgRestore), "PostgreSQL pg_restore 18 is required for this integration test.");

        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using (S3TestHost first = await scope.StartHostAsync())
        {
            await first.Storage.CreateBucketAsync("integration");
            using AmazonS3Client client = CreateClient(first.Endpoint);
            await client.PutObjectAsync(SdkPut("integration", "backup", "recoverable"));
        }

        string archive = Path.Combine(Path.GetTempPath(), "audorix-pg-" + Guid.NewGuid().ToString("N") + ".dump");
        string blobBackup = Path.Combine(Path.GetTempPath(), "audorix-blobs-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(await RunPostgreSqlToolAsync(pgDump, scope.ConnectionString,
                ["--format=custom", "--schema=" + scope.Schema, "--file=" + archive]));
            CopyDirectory(scope.Root, blobBackup);

            await scope.DropSchemaAsync();
            Directory.Delete(scope.Root, recursive: true);
            CopyDirectory(blobBackup, scope.Root);
            Assert.True(await RunPostgreSqlToolAsync(pgRestore, scope.ConnectionString,
                [archive]));

            await using S3TestHost restored = await scope.StartHostAsync();
            using AmazonS3Client client = CreateClient(restored.Endpoint);
            Assert.Equal("recoverable", await ReadStringAsync(client, "integration", "backup"));
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }

            if (Directory.Exists(blobBackup))
            {
                Directory.Delete(blobBackup, recursive: true);
            }
        }
    }

    [PostgreSqlFact]
    public async Task ManifestBackupRestoresIntoCleanSchemaAndRoot()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        string backup = Path.Combine(Path.GetTempPath(), "audorix-backup-" + Guid.NewGuid().ToString("N"));
        string restoredRoot = S3TestHost.CreateRoot();
        Directory.Delete(restoredRoot);
        PostgreSqlObjectStorageBackupService backupService;
        try
        {
            await using (S3TestHost host = await scope.StartHostAsync())
            {
                await host.Storage.CreateBucketAsync("integration");
                using AmazonS3Client client = CreateClient(host.Endpoint);
                await client.PutObjectAsync(new AwsPutObjectRequest
                {
                    BucketName = "integration",
                    Key = "one",
                    InputStream = new MemoryStream("first"u8.ToArray()),
                    ContentType = "text/plain",
                    UseChunkEncoding = false,
                    Metadata = { ["backup-marker"] = "preserved" }
                });
                await client.PutObjectAsync(SdkPut("integration", "two", "second"));
                backupService = host.Services.GetRequiredService<PostgreSqlObjectStorageBackupService>();
                ObjectStorageBackupManifest manifest = await backupService.CreateAsync(backup);
                Assert.Equal(2, manifest.ObjectCount);
                Assert.True(manifest.Complete);
                BackupValidationResult validation = await backupService.ValidateAsync(backup);
                Assert.Equal(2, validation.ValidatedObjectCount);
            }

            await scope.DropSchemaAsync();
            await backupService.RestoreAsync(backup, restoredRoot, scope.ConnectionString);
            await using S3TestHost restored = await S3TestHost.StartAsync(
                restoredRoot,
                deleteRoot: false,
                postgreSqlConnectionString: scope.ConnectionString,
                postgreSqlSchema: scope.Schema);
            using AmazonS3Client restoredClient = CreateClient(restored.Endpoint);
            Assert.Equal("first", await ReadStringAsync(restoredClient, "integration", "one"));
            Assert.Equal("second", await ReadStringAsync(restoredClient, "integration", "two"));
            GetObjectMetadataResponse restoredMetadata = await restoredClient.GetObjectMetadataAsync("integration", "one");
            Assert.Equal("text/plain", restoredMetadata.Headers.ContentType);
            Assert.Equal("preserved", restoredMetadata.Metadata["backup-marker"]);
        }
        finally
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            if (Directory.Exists(restoredRoot))
            {
                Directory.Delete(restoredRoot, recursive: true);
            }
        }
    }

    [PostgreSqlFact]
    public async Task BackupValidationRejectsCorruptionMixingUnsafeAndIncompleteSets()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        using AmazonS3Client client = CreateClient(host.Endpoint);
        await client.PutObjectAsync(SdkPut("integration", "one", "first"));
        PostgreSqlObjectStorageBackupService service = host.Services.GetRequiredService<PostgreSqlObjectStorageBackupService>();
        string parent = Path.Combine(Path.GetTempPath(), "audorix-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            string valid = Path.Combine(parent, "valid");
            await service.CreateAsync(valid);

            string missing = CloneDirectory(valid, Path.Combine(parent, "missing"));
            File.Delete(Directory.EnumerateFiles(Path.Combine(missing, "blobs")).Single());
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(missing));

            string corrupted = CloneDirectory(valid, Path.Combine(parent, "corrupted"));
            await File.AppendAllTextAsync(Directory.EnumerateFiles(Path.Combine(corrupted, "blobs")).Single(), "corrupt");
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(corrupted));

            string truncated = CloneDirectory(valid, Path.Combine(parent, "truncated"));
            using (FileStream dump = File.OpenWrite(Path.Combine(truncated, "metadata.dump")))
            {
                dump.SetLength(Math.Max(0, dump.Length / 2));
            }
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(truncated));

            string unsupported = CloneDirectory(valid, Path.Combine(parent, "unsupported"));
            await RewriteManifestAsync(unsupported, manifest => manifest with { FormatVersion = 999 });
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(unsupported));

            string unsafeSet = CloneDirectory(valid, Path.Combine(parent, "unsafe"));
            await RewriteManifestAsync(unsafeSet, manifest => manifest with
            {
                DatabaseDump = manifest.DatabaseDump with { Path = "../metadata.dump" }
            });
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(unsafeSet));

            string duplicate = CloneDirectory(valid, Path.Combine(parent, "duplicate"));
            string inventoryPath = Path.Combine(duplicate, "inventory.ndjson");
            string entry = (await File.ReadAllLinesAsync(inventoryPath)).Single();
            await File.AppendAllTextAsync(inventoryPath, entry + Environment.NewLine);
            await RefreshInventoryDescriptorAsync(duplicate, objectCountAdjustment: 1);
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(duplicate));

            string incomplete = Path.Combine(parent, "interrupted.incomplete");
            Directory.CreateDirectory(incomplete);
            await File.WriteAllTextAsync(Path.Combine(incomplete, "inventory.ndjson"), entry);
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(incomplete));

            await client.PutObjectAsync(SdkPut("integration", "two", "second-set"));
            string second = Path.Combine(parent, "second");
            await service.CreateAsync(second);
            string mixed = CloneDirectory(valid, Path.Combine(parent, "mixed"));
            string mixedBlob = Directory.EnumerateFiles(Path.Combine(mixed, "blobs")).Single();
            string replacementBlob = Directory.EnumerateFiles(Path.Combine(second, "blobs"))
                .Single(path => !string.Equals(Path.GetFileName(path), Path.GetFileName(mixedBlob), StringComparison.Ordinal));
            File.Copy(
                replacementBlob,
                mixedBlob,
                overwrite: true);
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(mixed));

            string mixedDump = CloneDirectory(valid, Path.Combine(parent, "mixed-dump"));
            File.Copy(Path.Combine(second, "metadata.dump"), Path.Combine(mixedDump, "metadata.dump"), overwrite: true);
            await Assert.ThrowsAsync<BackupValidationException>(() => service.ValidateAsync(mixedDump));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [PostgreSqlFact]
    public async Task MaintenanceRejectsMutationsAndFailedRestoreLeavesActiveStorageUntouched()
    {
        await using PostgreSqlTestScope scope = await PostgreSqlTestScope.CreateAsync();
        await using S3TestHost host = await scope.StartHostAsync();
        await host.Storage.CreateBucketAsync("integration");
        await host.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest(
            "integration", "protected", new MemoryStream("old"u8.ToArray())));

        await using (IAsyncDisposable maintenance = await host.Storage.EnterMaintenanceAsync())
        {
            await Assert.ThrowsAsync<ObjectStorageMaintenanceException>(() =>
                host.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest(
                    "integration", "blocked", new MemoryStream("new"u8.ToArray()))));
            await Assert.ThrowsAsync<ObjectStorageMaintenanceException>(() =>
                host.Storage.DeleteObjectAsync("integration", "protected"));
            await Assert.ThrowsAsync<ObjectStorageMaintenanceException>(() =>
                host.Storage.CleanupUnreferencedBlobsAsync());
        }

        string invalidBackup = Path.Combine(Path.GetTempPath(), "audorix-invalid-" + Guid.NewGuid().ToString("N"));
        string restoreRoot = Path.Combine(Path.GetTempPath(), "audorix-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(invalidBackup);
        try
        {
            PostgreSqlObjectStorageBackupService service = host.Services.GetRequiredService<PostgreSqlObjectStorageBackupService>();
            await Assert.ThrowsAsync<BackupValidationException>(() =>
                service.RestoreAsync(invalidBackup, restoreRoot, scope.ConnectionString));
            Assert.False(Directory.Exists(restoreRoot));
            await using ObjectReadResult preserved = await host.Storage.OpenReadAsync("integration", "protected");
            using StreamReader reader = new(preserved.Content);
            Assert.Equal("old", await reader.ReadToEndAsync());
            await host.Storage.PutObjectAsync(new Audorix.ObjectStorage.PutObjectRequest(
                "integration", "resumed", new MemoryStream("yes"u8.ToArray())));
        }
        finally
        {
            Directory.Delete(invalidBackup, recursive: true);
        }
    }

    [Fact]
    public async Task ObjectsRemainReadableAfterHostRestartAtConfiguredPath()
    {
        string root = S3TestHost.CreateRoot();
        await using (S3TestHost first = await S3TestHost.StartAsync(root, deleteRoot: false))
        {
            await first.Storage.CreateBucketAsync("integration");
            using AmazonS3Client client = CreateClient(first.Endpoint);
            await client.PutObjectAsync(new AwsPutObjectRequest
            {
                BucketName = "integration",
                Key = "restart",
                InputStream = new MemoryStream("persisted"u8.ToArray()),
                UseChunkEncoding = false
            });
        }

        await using S3TestHost second = await S3TestHost.StartAsync(root, deleteRoot: true);
        using AmazonS3Client reopened = CreateClient(second.Endpoint);
        using GetObjectResponse response = await reopened.GetObjectAsync("integration", "restart");
        using StreamReader reader = new(response.ResponseStream, Encoding.UTF8);
        Assert.Equal("persisted", await reader.ReadToEndAsync());
    }

    private static AmazonS3Client CreateClient(string endpoint, string accessKey = AccessKey, string secretKey = SecretKey) =>
        new(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint + "/s3",
            AuthenticationRegion = Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 0
        });

    private static AwsPutObjectRequest SdkPut(string bucket, string key, string value) => new()
    {
        BucketName = bucket,
        Key = key,
        InputStream = new MemoryStream(Encoding.UTF8.GetBytes(value)),
        UseChunkEncoding = false
    };

    private static async Task PutKeysAsync(AmazonS3Client client, IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            await client.PutObjectAsync(SdkPut("integration", key, key));
        }
    }

    private static async Task AssertInvalidTokenAsync(
        AmazonS3Client client,
        string bucket,
        string token,
        string? prefix = null)
    {
        AmazonS3Exception error = await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token
            }));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal("InvalidArgument", error.ErrorCode);
    }

    private static async Task<string> ReadStringAsync(AmazonS3Client client, string bucket, string key)
    {
        using GetObjectResponse response = await client.GetObjectAsync(bucket, key);
        using StreamReader reader = new(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream)
    {
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static HttpRequestMessage CreateSignedRequest(
        HttpMethod method,
        string url,
        string accessKey,
        string secretKey,
        byte[] signedBody,
        DateTimeOffset timestamp)
    {
        Uri uri = new(url);
        string amzDate = timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string shortDate = amzDate[..8];
        string payloadHash = Convert.ToHexStringLower(SHA256.HashData(signedBody));
        string canonicalPath = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        canonicalPath = "/" + canonicalPath;
        string canonicalHeaders = $"host:{uri.Authority}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        string canonicalRequest = $"{method.Method}\n{canonicalPath}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        string scope = $"{shortDate}/{Region}/s3/aws4_request";
        string stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        byte[] key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), shortDate), Region), "s3"), "aws4_request");
        string signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));

        HttpRequestMessage request = new(method, uri);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "AWS4-HMAC-SHA256",
            $"Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        if (method == HttpMethod.Put)
        {
            request.Content = new ByteArrayContent(signedBody);
        }

        return request;
    }

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    private static async Task<CommandResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (environment is not null)
            {
                foreach ((string name, string value) in environment)
                {
                    startInfo.Environment[name] = value;
                }
            }

            using Process process = Process.Start(startInfo)!;
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutput, standardError);
            return new CommandResult(process.ExitCode, standardOutput.Result, standardError.Result);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            return new CommandResult(-1, string.Empty, exception.Message);
        }
    }

    private static void AssertCommandSucceeded(CommandResult result) =>
        Assert.True(result.Succeeded, "Command failed: " + result.CombinedOutput);

    private static string? ResolveAwsCliPath()
    {
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Amazon", "AWSCLIV2", "aws.exe");
        if (File.Exists(local))
        {
            return local;
        }

        string programFiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Amazon", "AWSCLIV2", "aws.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public bool Succeeded => ExitCode == 0;
        public string CombinedOutput => (StandardOutput + Environment.NewLine + StandardError).Trim();
    }

    private static string? GetPostgreSqlConnectionString()
    {
        string? environmentValue = Environment.GetEnvironmentVariable("AUDORIX_OBJECTSTORAGE_POSTGRES");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
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
        return document.RootElement.TryGetProperty("Database", out JsonElement database)
            && database.TryGetProperty("ActiveConnectionString", out JsonElement connectionString)
            ? connectionString.GetString()
            : null;
    }

    private static async Task<bool> RunPostgreSqlToolAsync(
        string executable,
        string connectionString,
        IReadOnlyList<string> arguments)
    {
        NpgsqlConnectionStringBuilder connection = new(connectionString);
        ProcessStartInfo startInfo = new(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--host=" + connection.Host);
        startInfo.ArgumentList.Add("--port=" + connection.Port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--username=" + connection.Username);
        startInfo.ArgumentList.Add("--dbname=" + connection.Database);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(connection.Password))
        {
            startInfo.Environment["PGPASSWORD"] = connection.Password;
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(standardOutput, standardError);
        return process.ExitCode == 0;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string CloneDirectory(string source, string destination)
    {
        CopyDirectory(source, destination);
        return destination;
    }

    private static async Task RewriteManifestAsync(
        string backupDirectory,
        Func<ObjectStorageBackupManifest, ObjectStorageBackupManifest> update)
    {
        string path = Path.Combine(backupDirectory, "manifest.json");
        ObjectStorageBackupManifest manifest = JsonSerializer.Deserialize<ObjectStorageBackupManifest>(
            await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(update(manifest), new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private static async Task RefreshInventoryDescriptorAsync(string backupDirectory, long objectCountAdjustment)
    {
        string inventoryPath = Path.Combine(backupDirectory, "inventory.ndjson");
        byte[] hash;
        await using (FileStream stream = File.OpenRead(inventoryPath))
        {
            hash = await SHA256.HashDataAsync(stream);
        }

        await RewriteManifestAsync(backupDirectory, manifest => manifest with
        {
            Inventory = manifest.Inventory with
            {
                Length = new FileInfo(inventoryPath).Length,
                Sha256 = Convert.ToHexStringLower(hash)
            },
            ObjectCount = manifest.ObjectCount + objectCountAdjustment
        });
    }

    private sealed class SlowContent(byte[] content) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (int offset = 0; offset < content.Length; offset += 4096)
            {
                int count = Math.Min(4096, content.Length - offset);
                await stream.WriteAsync(content.AsMemory(offset, count));
                await Task.Delay(5);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = content.Length;
            return true;
        }
    }

    private sealed class Utf8StringComparer : IComparer<string>
    {
        public static Utf8StringComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        }
    }

    private sealed class AwsCliPostgreSqlFactAttribute : FactAttribute
    {
        public AwsCliPostgreSqlFactAttribute()
        {
            string? executable = ResolveAwsCliPath();
            if (executable is null)
            {
                Skip = "AWS CLI v2 is not installed in a supported location.";
                return;
            }

            if (string.IsNullOrWhiteSpace(GetPostgreSqlConnectionString()))
            {
                Skip = "Configure database.json or AUDORIX_OBJECTSTORAGE_POSTGRES to execute the PostgreSQL integration test.";
                return;
            }

            try
            {
                using Process process = Process.Start(new ProcessStartInfo(executable, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                if (!process.WaitForExit(5000) || process.ExitCode != 0)
                {
                    Skip = "AWS CLI is not installed or not callable.";
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Skip = "AWS CLI is not installed or not callable.";
            }
        }
    }

    private sealed class PostgreSqlFactAttribute : FactAttribute
    {
        public PostgreSqlFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(GetPostgreSqlConnectionString()))
            {
                Skip = "Configure database.json or AUDORIX_OBJECTSTORAGE_POSTGRES to execute the PostgreSQL integration test.";
            }
        }
    }

    private sealed class PostgreSqlTestScope : IAsyncDisposable
    {
        private PostgreSqlTestScope(string connectionString, string schema, string root)
        {
            ConnectionString = connectionString;
            Schema = schema;
            Root = root;
        }

        public string ConnectionString { get; }
        public string Schema { get; }
        public string Root { get; }

        public static Task<PostgreSqlTestScope> CreateAsync()
        {
            string connectionString = GetPostgreSqlConnectionString()
                ?? throw new InvalidOperationException("PostgreSQL test connection is not configured.");
            string schema = "audorix_s3_test_" + Guid.NewGuid().ToString("N");
            string root = S3TestHost.CreateRoot();
            return Task.FromResult(new PostgreSqlTestScope(connectionString, schema, root));
        }

        public Task<S3TestHost> StartHostAsync() =>
            S3TestHost.StartAsync(
                Root,
                deleteRoot: false,
                postgreSqlConnectionString: ConnectionString,
                postgreSqlSchema: Schema);

        public async Task DropSchemaAsync()
        {
            if (!Schema.StartsWith("audorix_s3_test_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a schema outside the test namespace.");
            }

            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new($"drop schema if exists \"{Schema}\" cascade;", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DropSchemaAsync();
            if (Directory.Exists(Root))
            {
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
    }

    private sealed class S3TestHost : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly bool _deleteRoot;

        private S3TestHost(WebApplication application, string endpoint, string root, bool deleteRoot)
        {
            _application = application;
            Endpoint = endpoint;
            Root = root;
            _deleteRoot = deleteRoot;
            Storage = application.Services.GetRequiredService<IObjectStorageEngine>();
        }

        public string Endpoint { get; }
        public string Root { get; }
        public IObjectStorageEngine Storage { get; }
        public IServiceProvider Services => _application.Services;

        public static string CreateRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "Audorix.ObjectStorage.S3.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        public static async Task<S3TestHost> StartAsync(
            string? root = null,
            bool deleteRoot = true,
            string? postgreSqlConnectionString = null,
            string? postgreSqlSchema = null,
            Action<S3EndpointOptions>? configureS3 = null)
        {
            root ??= CreateRoot();
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAudorixObjectStorage(options => options.StorageRootPath = root);
            if (string.IsNullOrWhiteSpace(postgreSqlConnectionString))
            {
                builder.Services.AddSingleton<IObjectStorageMetadataStore>(
                    new JsonFileMetadataStore(Path.Combine(root, "metadata.json")));
            }
            else
            {
                builder.Services.AddAudorixObjectStoragePostgreSql(options =>
                {
                    options.ConnectionString = postgreSqlConnectionString;
                    if (!string.IsNullOrWhiteSpace(postgreSqlSchema))
                    {
                        options.SchemaName = postgreSqlSchema;
                    }
                });
                builder.Services.AddAudorixObjectStoragePostgreSqlBackup(options =>
                {
                    options.PgDumpPath = @"C:\Program Files\PostgreSQL\18\bin\pg_dump.exe";
                    options.PgRestorePath = @"C:\Program Files\PostgreSQL\18\bin\pg_restore.exe";
                });
            }

            builder.Services.AddAudorixObjectStorageS3(options =>
                {
                    options.Region = Region;
                    options.ContinuationTokenSigningKey = TokenSigningKey;
                    options.Credentials.Add(new S3Credential
                    {
                        AccessKeyId = AccessKey,
                        SecretAccessKey = SecretKey,
                        AllowedBuckets = ["integration"],
                        AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
                    });
                    options.Credentials.Add(new S3Credential
                    {
                        AccessKeyId = ReadOnlyAccessKey,
                        SecretAccessKey = ReadOnlySecretKey,
                        AllowedBuckets = ["integration"],
                        AllowedOperations = S3Operation.GetObject | S3Operation.HeadObject
                    });
                    options.Credentials.Add(new S3Credential
                    {
                        AccessKeyId = ListAccessKey,
                        SecretAccessKey = ListSecretKey,
                        AllowedBuckets = ["integration"],
                        AllowedOperations = S3Operation.ListBucket
                    });
                    configureS3?.Invoke(options);
                });

            WebApplication app = builder.Build();
            app.MapAudorixObjectStorageS3("/s3");
            await app.StartAsync();
            string endpoint = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new S3TestHost(app, endpoint, root, deleteRoot);
        }

        public void AllowBucket(string bucket)
        {
            S3EndpointOptions options = _application.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3EndpointOptions>>().Value;
            options.Credentials.Single(credential => credential.AccessKeyId == AccessKey).AllowedBuckets.Add(bucket);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.DisposeAsync();
            if (_deleteRoot)
            {
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
    }
}
