using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Audorix.ObjectStorage;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using AwsPutObjectRequest = Amazon.S3.Model.PutObjectRequest;

const string AccessKey = "AUDORIXLOADHARNESS";
const string SecretKey = "audorix-load-harness-secret";
const string Region = "us-east-1";
const string Bucket = "measurements";
const string TokenKey = "audorix-load-harness-token-signing-key-v1";
const int CopyBufferSize = 128 * 1024;

if (args.Contains("--external-performance", StringComparer.OrdinalIgnoreCase))
{
    await ExternalPerformanceHarness.RunAsync(args);
    return;
}

if (args.FirstOrDefault() == "soak-server")
{
    await SoakHarness.RunServerAsync(args.Skip(1).ToArray());
    return;
}

if (args.Contains("--soak", StringComparer.OrdinalIgnoreCase))
{
    await SoakHarness.RunAsync(args);
    return;
}

if (args.Contains("--overload", StringComparer.OrdinalIgnoreCase))
{
    await RunOverloadHarnessAsync(args);
    return;
}

if (args.Contains("--listing", StringComparer.OrdinalIgnoreCase))
{
    await RunListingHarnessAsync(args);
    return;
}

if (args.Contains("--recovery", StringComparer.OrdinalIgnoreCase))
{
    await RecoveryHarness.RunAsync(args);
    return;
}

if (args.FirstOrDefault() == "server")
{
    await RunServerAsync(args[1], args[2], args[3]);
    return;
}

if (args.FirstOrDefault() == "drop-schema")
{
    await DropSchemaAsync(LoadConnectionString(), args[1]);
    return;
}

if (args.Contains("--duration-seconds", StringComparer.OrdinalIgnoreCase))
{
    await RunSustainedHarnessAsync(args);
}
else
{
    await RunHarnessAsync(args);
}

async Task RunServerAsync(string root, string schema, string readyFile)
{
    string connectionString = Environment.GetEnvironmentVariable("AUDORIX_OBJECTSTORAGE_POSTGRES")
        ?? throw new InvalidOperationException("AUDORIX_OBJECTSTORAGE_POSTGRES is required.");
    WebApplicationBuilder builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
    builder.Logging.SetMinimumLevel(LogLevel.Error);
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
    builder.Services.AddAudorixObjectStorage(options =>
    {
        options.StorageRootPath = root;
        options.CopyBufferSize = CopyBufferSize;
    });
    builder.Services.AddAudorixObjectStoragePostgreSql(options =>
    {
        options.ConnectionString = connectionString;
        options.SchemaName = schema;
    });
    builder.Services.AddAudorixObjectStorageS3(options =>
    {
        options.Region = Region;
        options.MaxObjectSize = 512L * 1024 * 1024;
        options.MaxConcurrentOperations = int.Parse(Environment.GetEnvironmentVariable("AUDORIX_HARNESS_MAX_CONCURRENT") ?? "32");
        options.MaxQueuedOperations = int.Parse(Environment.GetEnvironmentVariable("AUDORIX_HARNESS_MAX_QUEUED") ?? "8");
        options.ContinuationTokenSigningKey = TokenKey;
        options.Credentials.Add(new S3Credential
        {
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
            AllowedBuckets = [Bucket],
            AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
        });
    });

    WebApplication app = builder.Build();
    app.MapGet("/harness/metrics", async (bool collect) =>
    {
        if (collect)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        using Process process = Process.GetCurrentProcess();
        int stagingFiles = Directory.Exists(Path.Combine(root, "staging"))
            ? Directory.EnumerateFiles(Path.Combine(root, "staging"), "*.staging").Count()
            : 0;
        await using Npgsql.NpgsqlConnection database = new(connectionString);
        await database.OpenAsync();
        await using Npgsql.NpgsqlCommand command = new("select count(*)::int, pg_database_size(current_database()), version();", database);
        await using Npgsql.NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return Results.Json(new ProcessMetrics(
            GC.GetTotalMemory(false),
            process.WorkingSet64,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            process.TotalProcessorTime.TotalMilliseconds,
            GetDirectorySize(root),
            stagingFiles,
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetString(2)));
    });
    app.MapPost("/harness/stop", (IHostApplicationLifetime lifetime) =>
    {
        lifetime.StopApplication();
        return Results.Ok();
    });
    app.MapAudorixObjectStorageS3("/s3");
    await app.StartAsync();
    IObjectStorageEngine storage = app.Services.GetRequiredService<IObjectStorageEngine>();
    await storage.CreateBucketAsync(Bucket);
    string endpoint = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    await File.WriteAllTextAsync(readyFile, endpoint);
    await app.WaitForShutdownAsync();
}

async Task RunOverloadHarnessAsync(string[] arguments)
{
    Dictionary<string, string> values = arguments
        .Where(value => !string.Equals(value, "--overload", StringComparison.OrdinalIgnoreCase))
        .Chunk(2)
        .Where(chunk => chunk.Length == 2)
        .ToDictionary(chunk => chunk[0], chunk => chunk[1], StringComparer.OrdinalIgnoreCase);
    int clients = int.Parse(values.GetValueOrDefault("--clients", "16"));
    int objectMiB = int.Parse(values.GetValueOrDefault("--object-mib", "8"));
    int maxConcurrent = int.Parse(values.GetValueOrDefault("--max-concurrent", "4"));
    int maxQueued = int.Parse(values.GetValueOrDefault("--max-queued", "2"));
    string work = Path.GetFullPath(values.GetValueOrDefault("--work", Path.GetTempPath()));
    string output = Path.GetFullPath(values.GetValueOrDefault("--output", Path.Combine(Directory.GetCurrentDirectory(), "overload-results.json")));
    if (clients < 2 || clients > 64 || objectMiB < 1 || objectMiB > 64 || maxConcurrent < 1 || maxQueued < 0)
        throw new ArgumentException("Invalid overload bounds.");

    string connectionString = LoadConnectionString();
    string runId = Guid.NewGuid().ToString("N");
    string root = Path.Combine(work, "audorix-overload-" + runId);
    string schema = "audorix_overload_" + runId;
    string readyFile = Path.Combine(work, "audorix-overload-ready-" + runId + ".txt");
    Directory.CreateDirectory(root);
    Process server = StartServer(root, schema, readyFile, connectionString, maxConcurrent, maxQueued);
    int completed = 0;
    int rejected = 0;
    ConcurrentBag<string> unexpected = [];
    Stopwatch elapsed = new();
    try
    {
        string endpoint = await WaitForEndpointAsync(readyFile, server);
        using AmazonS3Client client = CreateClient(endpoint);
        byte[] content = CreateSustainedContent(objectMiB * 1024 * 1024, 41, 73);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] offered = Enumerable.Range(0, clients).Select(async index =>
        {
            await release.Task;
            try
            {
                await using MemoryStream source = new(content, writable: false);
                await using ThrottledReadStream slow = new(source, TimeSpan.FromMilliseconds(8));
                await client.PutObjectAsync(new AwsPutObjectRequest
                {
                    BucketName = Bucket,
                    Key = $"overload/{index:D2}.bin",
                    InputStream = slow,
                    UseChunkEncoding = false
                });
                Interlocked.Increment(ref completed);
            }
            catch (AmazonS3Exception exception) when ((int)exception.StatusCode == 503 && exception.ErrorCode == "SlowDown")
            {
                Interlocked.Increment(ref rejected);
            }
            catch (Exception exception)
            {
                unexpected.Add(exception.GetType().Name + ": " + exception.Message);
            }
        }).ToArray();
        elapsed.Start();
        release.SetResult();
        await Task.WhenAll(offered);
        elapsed.Stop();

        byte[] recovery = "admission-recovered"u8.ToArray();
        await client.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = Bucket,
            Key = "overload/recovery.txt",
            InputStream = new MemoryStream(recovery, writable: false),
            UseChunkEncoding = false
        });
        using GetObjectResponse response = await client.GetObjectAsync(Bucket, "overload/recovery.txt");
        using MemoryStream read = new();
        await response.ResponseStream.CopyToAsync(read);
        bool recovered = read.ToArray().AsSpan().SequenceEqual(recovery);
        await client.DeleteObjectAsync(Bucket, "overload/recovery.txt");

        using HttpClient metricsClient = new() { BaseAddress = new Uri(endpoint) };
        ProcessMetrics metrics = await GetMetricsAsync(metricsClient, collect: false);
        var report = new
        {
            completedAt = DateTimeOffset.UtcNow,
            offeredClients = clients,
            objectBytes = content.Length,
            maxConcurrentOperations = maxConcurrent,
            maxQueuedOperations = maxQueued,
            elapsed = elapsed.Elapsed,
            attempted = clients,
            completed,
            expectedSlowDownRejections = rejected,
            unexpectedFailures = unexpected.Count,
            unexpected = unexpected.ToArray(),
            recoveryRequestSucceeded = recovered,
            serverMetrics = metrics,
            passed = rejected > 0 && completed > 0 && unexpected.IsEmpty && recovered
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine(output);
        if (!report.passed) throw new InvalidOperationException("Overload acceptance criteria were not met.");
        await metricsClient.PostAsync("/harness/stop", null);
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    finally
    {
        if (!server.HasExited)
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
        TryDeleteDirectory(root);
        if (File.Exists(readyFile)) File.Delete(readyFile);
        await DropSchemaAsync(connectionString, schema);
        server.Dispose();
    }
}

async Task RunListingHarnessAsync(string[] arguments)
{
    Dictionary<string, string> values = arguments
        .Where(value => !string.Equals(value, "--listing", StringComparison.OrdinalIgnoreCase))
        .Chunk(2)
        .Where(chunk => chunk.Length == 2)
        .ToDictionary(chunk => chunk[0], chunk => chunk[1], StringComparer.OrdinalIgnoreCase);
    int[] counts = values.GetValueOrDefault("--counts", "10,100,1000").Split(',').Select(int.Parse).ToArray();
    int repetitions = int.Parse(values.GetValueOrDefault("--repetitions", "20"));
    int pageSize = int.Parse(values.GetValueOrDefault("--page-size", "100"));
    string work = Path.GetFullPath(values.GetValueOrDefault("--work", Path.GetTempPath()));
    string output = Path.GetFullPath(values.GetValueOrDefault("--output", Path.Combine(Directory.GetCurrentDirectory(), "listing-results.json")));
    if (counts.Length == 0 || counts.Any(value => value < 1) || counts.Max() > 5000 || repetitions < 1 || repetitions > 100 || pageSize < 1 || pageSize > 1000)
        throw new ArgumentException("Invalid listing bounds.");

    string connectionString = LoadConnectionString();
    string runId = Guid.NewGuid().ToString("N");
    string root = Path.Combine(work, "audorix-listing-" + runId);
    string schema = "audorix_listing_" + runId;
    string readyFile = Path.Combine(work, "audorix-listing-ready-" + runId + ".txt");
    Directory.CreateDirectory(root);
    Process server = StartServer(root, schema, readyFile, connectionString);
    List<object> stages = [];
    try
    {
        string endpoint = await WaitForEndpointAsync(readyFile, server);
        using AmazonS3Client client = CreateClient(endpoint);
        byte[] content = new byte[1024];
        int seeded = 0;
        foreach (int target in counts.Order())
        {
            while (seeded < target)
            {
                int batchStart = seeded;
                int batchCount = Math.Min(8, target - seeded);
                await Task.WhenAll(Enumerable.Range(batchStart, batchCount).Select(index =>
                    client.PutObjectAsync(new AwsPutObjectRequest
                    {
                        BucketName = Bucket,
                        Key = $"listing/item-{index:D5}.bin",
                        InputStream = new MemoryStream(content, writable: false),
                        UseChunkEncoding = false
                    })));
                seeded += batchCount;
            }
            List<double> latencies = [];
            long returned = 0;
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                Stopwatch timer = Stopwatch.StartNew();
                string? token = null;
                int observed = 0;
                do
                {
                    ListObjectsV2Response page = await client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = Bucket,
                        Prefix = "listing/",
                        MaxKeys = pageSize,
                        ContinuationToken = token
                    });
                    observed += page.S3Objects.Count;
                    token = page.IsTruncated == true ? page.NextContinuationToken : null;
                } while (token is not null);
                timer.Stop();
                if (observed != target) throw new InvalidDataException($"Listing expected {target}, observed {observed}.");
                returned += observed;
                latencies.Add(timer.Elapsed.TotalMilliseconds);
            }
            double[] sorted = latencies.Order().ToArray();
            stages.Add(new
            {
                objectCount = target,
                repetitions,
                pageSize,
                attempted = repetitions,
                completed = repetitions,
                returnedObjects = returned,
                p50Milliseconds = Percentile(sorted, 0.50),
                p95Milliseconds = Percentile(sorted, 0.95),
                p99Milliseconds = Percentile(sorted, 0.99),
                requestsPerSecond = repetitions / Math.Max(latencies.Sum() / 1000.0, 0.001)
            });
        }
        using HttpClient metricsClient = new() { BaseAddress = new Uri(endpoint) };
        ProcessMetrics metrics = await GetMetricsAsync(metricsClient, collect: false);
        var report = new
        {
            completedAt = DateTimeOffset.UtcNow,
            maxObjects = counts.Max(),
            retainedBytes = counts.Max() * content.Length,
            stages,
            serverMetrics = metrics,
            failures = 0,
            passed = true
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine(output);
        await metricsClient.PostAsync("/harness/stop", null);
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    finally
    {
        if (!server.HasExited)
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
        TryDeleteDirectory(root);
        if (File.Exists(readyFile)) File.Delete(readyFile);
        await DropSchemaAsync(connectionString, schema);
        server.Dispose();
    }
}

async Task RunHarnessAsync(string[] arguments)
{
    HarnessOptions options = HarnessOptions.Parse(arguments);
    string connectionString = LoadConnectionString();
    DriveInfo drive = new(Path.GetPathRoot(options.WorkDirectory)!);
    long required = checked(options.LargeBytes * (options.MaxConcurrency * 2L + 1L) + 2L * 1024 * 1024 * 1024);
    if (drive.AvailableFreeSpace < required)
    {
        throw new InvalidOperationException(
            $"Insufficient free space. Required safety estimate: {required:N0} bytes; available: {drive.AvailableFreeSpace:N0} bytes.");
    }

    string runId = Guid.NewGuid().ToString("N");
    string root = Path.Combine(options.WorkDirectory, "audorix-load-" + runId);
    string schema = "audorix_load_" + runId;
    string readyFile = Path.Combine(options.WorkDirectory, "audorix-load-ready-" + runId + ".txt");
    Directory.CreateDirectory(root);
    Process server = StartServer(root, schema, readyFile, connectionString);
    try
    {
        string endpoint = await WaitForEndpointAsync(readyFile, server);
        using HttpClient metricsClient = new() { BaseAddress = new Uri(endpoint) };
        using AmazonS3Client client = CreateClient(endpoint);
        string outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string warmup = Path.Combine(root, "warmup-source.bin");
        (long _, string warmupHash) = await GenerateFileAsync(warmup, 8L * 1024 * 1024);
        await UploadAsync(client, warmup, "warmup", CancellationToken.None);
        string warmupDownload = Path.Combine(root, "warmup-download.bin");
        string warmupDownloadedHash = await DownloadAsync(client, "warmup", warmupDownload, CancellationToken.None);
        EnsureEqual(warmupHash, warmupDownloadedHash, "Warm-up hash");
        await client.DeleteObjectAsync(Bucket, "warmup");
        File.Delete(warmup);
        File.Delete(warmupDownload);

        List<TransferMeasurement> measurements = [];
        foreach (long size in new[] { options.ComparisonBytes, options.LargeBytes }.Distinct())
        {
            string source = Path.Combine(root, "source-" + size + ".bin");
            (long length, string expectedHash) = await GenerateFileAsync(source, size);
            foreach (int concurrency in options.Concurrency)
            {
                string prefix = $"object-{size}-{concurrency}-";
                measurements.Add(await MeasureAsync(
                    metricsClient,
                    "upload",
                    length,
                    concurrency,
                    () => Task.WhenAll(Enumerable.Range(0, concurrency)
                        .Select(index => UploadAsync(client, source, prefix + index, CancellationToken.None)))));

                measurements.Add(await MeasureAsync(
                    metricsClient,
                    "download",
                    length,
                    concurrency,
                    async () =>
                    {
                        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async index =>
                        {
                            string destination = Path.Combine(root, $"download-{size}-{concurrency}-{index}.bin");
                            string actualHash = await DownloadAsync(client, prefix + index, destination, CancellationToken.None);
                            EnsureEqual(expectedHash, actualHash, "Download hash");
                            if (new FileInfo(destination).Length != length)
                            {
                                throw new InvalidDataException("Downloaded length does not match the source.");
                            }

                            File.Delete(destination);
                        }));
                    }));

                const int rangeLength = 4096;
                measurements.Add(await MeasureAsync(
                    metricsClient,
                    $"range-{rangeLength}-from-{length}",
                    rangeLength,
                    concurrency,
                    () => Task.WhenAll(Enumerable.Range(0, concurrency)
                        .Select(index => DownloadRangeAsync(client, prefix + index, length - rangeLength, rangeLength, CancellationToken.None)))));

                foreach (int index in Enumerable.Range(0, concurrency))
                {
                    await client.DeleteObjectAsync(Bucket, prefix + index);
                }
            }

            File.Delete(source);
        }

        await VerifyCancelledOverwriteAsync(client, root, options.ComparisonBytes);
        string[] stagingFiles = Directory.GetFiles(Path.Combine(root, "staging"), "*.staging");
        if (stagingFiles.Length != 0)
        {
            throw new InvalidDataException("Staging files remained after cancellation.");
        }

        MemoryBudget budget = new(
            ManagedHeapBaseBytes: 128L * 1024 * 1024,
            ManagedHeapPerTransferBytes: 16L * 1024 * 1024,
            WorkingSetBaseBytes: 512L * 1024 * 1024,
            WorkingSetPerTransferBytes: 64L * 1024 * 1024);
        foreach (TransferMeasurement measurement in measurements)
        {
            long managedBudget = budget.ManagedHeapBaseBytes + budget.ManagedHeapPerTransferBytes * measurement.Concurrency;
            long workingSetBudget = budget.WorkingSetBaseBytes + budget.WorkingSetPerTransferBytes * measurement.Concurrency;
            if (measurement.ServerManagedHeapGrowth > managedBudget || measurement.ServerWorkingSetGrowth > workingSetBudget)
            {
                throw new InvalidOperationException(
                    $"Memory budget exceeded for {measurement.Direction}, {measurement.Bytes} bytes, concurrency {measurement.Concurrency}.");
            }
        }

        HarnessReport report = new(
            DateTimeOffset.UtcNow,
            Environment.Version.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            CopyBufferSize,
            ClientMaxErrorRetry: 0,
            "100 ms HTTP sampling from a separate client process after an 8 MiB warm-up; baseline forces full server and client GC.",
            budget,
            CancelledOverwritePreserved: true,
            StagingFilesAfterCancellation: stagingFiles.Length,
            measurements);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine(outputPath);

        await metricsClient.PostAsync("/harness/stop", null);
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    finally
    {
        if (!server.HasExited)
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }

        TryDeleteDirectory(root);
        if (File.Exists(readyFile))
        {
            File.Delete(readyFile);
        }

        await DropSchemaAsync(connectionString, schema);
        server.Dispose();
    }
}

async Task RunSustainedHarnessAsync(string[] arguments)
{
    SustainedOptions options = SustainedOptions.Parse(arguments);
    string connectionString = LoadConnectionString();
    string runId = Guid.NewGuid().ToString("N");
    string root = Path.Combine(options.WorkDirectory, "audorix-sustained-" + runId);
    string schema = "audorix_sustained_" + runId;
    string readyFile = Path.Combine(options.WorkDirectory, "audorix-sustained-ready-" + runId + ".txt");
    Directory.CreateDirectory(root);
    Process server = StartServer(root, schema, readyFile, connectionString);
    Stopwatch elapsed = Stopwatch.StartNew();
    ConcurrentDictionary<string, ConcurrentBag<double>> latencies = new(StringComparer.Ordinal);
    ConcurrentDictionary<string, long> counts = new(StringComparer.Ordinal);
    ConcurrentBag<string> failures = [];
    long verifiedHashes = 0;
    try
    {
        string endpoint = await WaitForEndpointAsync(readyFile, server);
        using HttpClient metricsClient = new() { BaseAddress = new Uri(endpoint) };
        ProcessMetrics serverStart = await GetMetricsAsync(metricsClient, collect: true);
        elapsed.Restart();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(options.Duration);
        Task[] workers = Enumerable.Range(0, options.Concurrency).Select(async worker =>
        {
            using AmazonS3Client client = CreateClient(endpoint);
            int sequence = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                string key = $"sustained/{runId}/worker-{worker}/object-{sequence++}.bin";
                byte[] content = CreateSustainedContent(options.ObjectBytes, worker, sequence);
                string expectedHash = Convert.ToHexStringLower(SHA256.HashData(content));
                try
                {
                    await MeasureOperationAsync("upload", latencies, counts, async () =>
                    {
                        await client.PutObjectAsync(new AwsPutObjectRequest
                        {
                            BucketName = Bucket,
                            Key = key,
                            InputStream = new MemoryStream(content, writable: false),
                            UseChunkEncoding = false
                        });
                    });
                    await MeasureOperationAsync("read", latencies, counts, async () =>
                    {
                        using GetObjectResponse response = await client.GetObjectAsync(Bucket, key);
                        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        byte[] buffer = new byte[CopyBufferSize];
                        int read;
                        while ((read = await response.ResponseStream.ReadAsync(buffer)) > 0) hash.AppendData(buffer, 0, read);
                        EnsureEqual(expectedHash, Convert.ToHexStringLower(hash.GetHashAndReset()), "Sustained read hash");
                        Interlocked.Increment(ref verifiedHashes);
                    });
                    await MeasureOperationAsync("list", latencies, counts, async () =>
                    {
                        ListObjectsV2Response page = await client.ListObjectsV2Async(new ListObjectsV2Request
                        {
                            BucketName = Bucket,
                            Prefix = $"sustained/{runId}/worker-{worker}/",
                            MaxKeys = 1000
                        });
                        if (!page.S3Objects.Any(item => item.Key == key)) throw new InvalidDataException("Sustained list omitted the uploaded object.");
                    });
                    await MeasureOperationAsync("range", latencies, counts, async () =>
                    {
                        int rangeLength = Math.Min(4096, content.Length);
                        using GetObjectResponse response = await client.GetObjectAsync(new GetObjectRequest
                        {
                            BucketName = Bucket,
                            Key = key,
                            ByteRange = new ByteRange(0, rangeLength - 1)
                        });
                        byte[] actual = new byte[rangeLength];
                        await response.ResponseStream.ReadExactlyAsync(actual);
                        if (!actual.AsSpan().SequenceEqual(content.AsSpan(0, rangeLength))) throw new InvalidDataException("Sustained range bytes differ.");
                    });
                    await MeasureOperationAsync("delete", latencies, counts, async () =>
                    {
                        await client.DeleteObjectAsync(Bucket, key);
                    });
                }
                catch (Exception exception)
                {
                    failures.Add($"worker={worker} key={key} {exception.GetType().Name}: {exception.Message}");
                    try { await client.DeleteObjectAsync(Bucket, key); } catch { }
                }
            }
        }).ToArray();
        await Task.WhenAll(workers);
        elapsed.Stop();
        ProcessMetrics serverEnd = await GetMetricsAsync(metricsClient, collect: false);

        List<SustainedOperationSummary> summaries = latencies.OrderBy(pair => pair.Key).Select(pair =>
        {
            double[] samples = pair.Value.Order().ToArray();
            return new SustainedOperationSummary(
                pair.Key,
                counts.GetValueOrDefault(pair.Key),
                samples.Min(),
                Percentile(samples, 0.50),
                Percentile(samples, 0.95),
                Percentile(samples, 0.99),
                samples.Max());
        }).ToList();
        SustainedReport report = new(
            DateTimeOffset.UtcNow,
            options.Duration,
            elapsed.Elapsed,
            options.Concurrency,
            options.ObjectBytes,
            counts.Values.Sum() + failures.Count,
            counts.Values.Sum(),
            counts.Values.Sum() / elapsed.Elapsed.TotalSeconds,
            checked(verifiedHashes * options.ObjectBytes * 2),
            verifiedHashes,
            serverStart,
            serverEnd,
            Math.Max(0, serverEnd.TotalProcessorMilliseconds - serverStart.TotalProcessorMilliseconds) / elapsed.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
            failures.Count,
            failures.Take(20).ToArray(),
            summaries);
        string outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        Console.WriteLine(outputPath);
        if (!failures.IsEmpty) throw new InvalidOperationException($"Sustained workload recorded {failures.Count} failures.");

        using HttpClient stopClient = new() { BaseAddress = new Uri(endpoint) };
        await stopClient.PostAsync("/harness/stop", null);
        await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    }
    finally
    {
        if (!server.HasExited)
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
        TryDeleteDirectory(root);
        if (File.Exists(readyFile)) File.Delete(readyFile);
        await DropSchemaAsync(connectionString, schema);
        server.Dispose();
    }
}

static async Task MeasureOperationAsync(
    string operation,
    ConcurrentDictionary<string, ConcurrentBag<double>> latencies,
    ConcurrentDictionary<string, long> counts,
    Func<Task> action)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    await action();
    stopwatch.Stop();
    latencies.GetOrAdd(operation, _ => []).Add(stopwatch.Elapsed.TotalMilliseconds);
    counts.AddOrUpdate(operation, 1, (_, value) => value + 1);
}

static byte[] CreateSustainedContent(int length, int worker, int sequence)
{
    byte[] content = new byte[length];
    for (int index = 0; index < content.Length; index++) content[index] = (byte)((index * 31 + worker * 17 + sequence * 13) % 251);
    return content;
}

static double Percentile(double[] sorted, double percentile)
{
    int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
    return sorted[index];
}

async Task<TransferMeasurement> MeasureAsync(
    HttpClient metricsClient,
    string direction,
    long bytes,
    int concurrency,
    Func<Task> transfer)
{
    ProcessMetrics serverBaseline = await GetMetricsAsync(metricsClient, collect: true);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    using Process current = Process.GetCurrentProcess();
    ProcessMetrics clientBaseline = new(GC.GetTotalMemory(false), current.WorkingSet64, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    ProcessMetrics serverPeak = serverBaseline;
    ProcessMetrics clientPeak = clientBaseline;
    using CancellationTokenSource sampling = new();
    Task sampler = Task.Run(async () =>
    {
        while (!sampling.IsCancellationRequested)
        {
            try
            {
                ProcessMetrics serverSample = await GetMetricsAsync(metricsClient, collect: false);
                serverPeak = ProcessMetrics.Max(serverPeak, serverSample);
                using Process clientProcess = Process.GetCurrentProcess();
                ProcessMetrics clientSample = new(
                    GC.GetTotalMemory(false),
                    clientProcess.WorkingSet64,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2));
                clientPeak = ProcessMetrics.Max(clientPeak, clientSample);
                await Task.Delay(100, sampling.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    });

    Stopwatch stopwatch = Stopwatch.StartNew();
    await transfer();
    stopwatch.Stop();
    sampling.Cancel();
    await sampler;
    serverPeak = ProcessMetrics.Max(serverPeak, await GetMetricsAsync(metricsClient, collect: false));
    double totalMiB = bytes * (double)concurrency / (1024 * 1024);
    return new TransferMeasurement(
        direction,
        bytes,
        concurrency,
        stopwatch.Elapsed,
        totalMiB / stopwatch.Elapsed.TotalSeconds,
        serverBaseline,
        serverPeak,
        clientBaseline,
        clientPeak);
}

async Task VerifyCancelledOverwriteAsync(AmazonS3Client client, string root, long size)
{
    string previous = Path.Combine(root, "previous.bin");
    await File.WriteAllBytesAsync(previous, "previous"u8.ToArray());
    await UploadAsync(client, previous, "cancelled-overwrite", CancellationToken.None);
    string large = Path.Combine(root, "cancel-source.bin");
    await GenerateFileAsync(large, size);
    await using FileStream file = File.OpenRead(large);
    await using ThrottledReadStream throttled = new(file, TimeSpan.FromMilliseconds(2));
    using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
    await AssertCancelledAsync(() => client.PutObjectAsync(new AwsPutObjectRequest
    {
        BucketName = Bucket,
        Key = "cancelled-overwrite",
        InputStream = throttled,
        UseChunkEncoding = false
    }, cancellation.Token));
    string destination = Path.Combine(root, "cancel-check.bin");
    string actualHash = await DownloadAsync(client, "cancelled-overwrite", destination, CancellationToken.None);
    EnsureEqual(Convert.ToHexStringLower(SHA256.HashData("previous"u8)), actualHash, "Cancelled overwrite preservation");
    File.Delete(previous);
    File.Delete(large);
    File.Delete(destination);
}

static async Task AssertCancelledAsync(Func<Task> operation)
{
    try
    {
        await operation();
        throw new InvalidOperationException("The large overwrite completed before cancellation.");
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task UploadAsync(AmazonS3Client client, string path, string key, CancellationToken cancellationToken)
{
    await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    await client.PutObjectAsync(new AwsPutObjectRequest
    {
        BucketName = Bucket,
        Key = key,
        InputStream = input,
        UseChunkEncoding = false
    }, cancellationToken);
}

static async Task<string> DownloadAsync(AmazonS3Client client, string key, string destination, CancellationToken cancellationToken)
{
    using GetObjectResponse response = await client.GetObjectAsync(Bucket, key, cancellationToken);
    await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] buffer = new byte[CopyBufferSize];
    int read;
    while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
    {
        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        hash.AppendData(buffer, 0, read);
    }

    await output.FlushAsync(cancellationToken);
    return Convert.ToHexStringLower(hash.GetHashAndReset());
}

static async Task DownloadRangeAsync(AmazonS3Client client, string key, long start, int length, CancellationToken cancellationToken)
{
    using GetObjectResponse response = await client.GetObjectAsync(new GetObjectRequest
    {
        BucketName = Bucket,
        Key = key,
        ByteRange = new ByteRange(start, start + length - 1)
    }, cancellationToken);
    byte[] content = new byte[length];
    await response.ResponseStream.ReadExactlyAsync(content, cancellationToken);
    if (response.ContentLength != length || response.ResponseStream.ReadByte() != -1)
        throw new InvalidDataException("Range response transferred an unexpected number of bytes.");
}

static async Task<(long Length, string Hash)> GenerateFileAsync(string path, long length)
{
    byte[] buffer = new byte[CopyBufferSize];
    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    long written = 0;
    while (written < length)
    {
        int count = (int)Math.Min(buffer.Length, length - written);
        for (int index = 0; index < count; index++)
        {
            buffer[index] = (byte)((written + index) * 31 % 251);
        }

        await output.WriteAsync(buffer.AsMemory(0, count));
        hash.AppendData(buffer, 0, count);
        written += count;
    }

    await output.FlushAsync();
    return (written, Convert.ToHexStringLower(hash.GetHashAndReset()));
}

static AmazonS3Client CreateClient(string endpoint) =>
    new(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config
    {
        ServiceURL = endpoint + "/s3",
        AuthenticationRegion = Region,
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        MaxErrorRetry = 0
    });

static Process StartServer(string root, string schema, string readyFile, string connectionString, int maxConcurrent = 32, int maxQueued = 8)
{
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve harness executable.");
    ProcessStartInfo startInfo = new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.ArgumentList.Add("server");
    startInfo.ArgumentList.Add(root);
    startInfo.ArgumentList.Add(schema);
    startInfo.ArgumentList.Add(readyFile);
    startInfo.Environment["AUDORIX_OBJECTSTORAGE_POSTGRES"] = connectionString;
    startInfo.Environment["AUDORIX_HARNESS_MAX_CONCURRENT"] = maxConcurrent.ToString();
    startInfo.Environment["AUDORIX_HARNESS_MAX_QUEUED"] = maxQueued.ToString();
    Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Cannot start the measurement server.");
    process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Console.Error.WriteLine("server: " + eventArgs.Data); };
    process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Console.Error.WriteLine("server: " + eventArgs.Data); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}

static async Task<string> WaitForEndpointAsync(string readyFile, Process server)
{
    for (int attempt = 0; attempt < 300; attempt++)
    {
        if (File.Exists(readyFile))
        {
            try
            {
                string endpoint = await File.ReadAllTextAsync(readyFile);
                if (!string.IsNullOrWhiteSpace(endpoint)) return endpoint;
            }
            catch (IOException)
            {
                // The child may still hold the readiness file for its single atomic-sized write.
            }
        }

        if (server.HasExited)
        {
            throw new InvalidOperationException("Measurement server exited before becoming ready.");
        }

        await Task.Delay(100);
    }

    throw new TimeoutException("Measurement server did not become ready.");
}

static async Task<ProcessMetrics> GetMetricsAsync(HttpClient client, bool collect)
{
    using HttpResponseMessage response = await client.GetAsync("/harness/metrics?collect=" + collect.ToString().ToLowerInvariant());
    response.EnsureSuccessStatusCode();
    return (await response.Content.ReadFromJsonAsync<ProcessMetrics>())!;
}

static string LoadConnectionString()
{
    string? configured = Environment.GetEnvironmentVariable("AUDORIX_OBJECTSTORAGE_POSTGRES");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    string path = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Audorix.ObjectStorage.PostgreSql", "data", "database.json"));
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.GetProperty("Database").GetProperty("ActiveConnectionString").GetString()
        ?? throw new InvalidOperationException("The PostgreSQL connection string is missing.");
}

static async Task DropSchemaAsync(string connectionString, string schema)
{
    await using Npgsql.NpgsqlConnection connection = new(connectionString);
    await connection.OpenAsync();
    await using Npgsql.NpgsqlCommand command = new($"drop schema if exists \"{schema}\" cascade;", connection);
    await command.ExecuteNonQueryAsync();
}

static void EnsureEqual(string expected, string actual, string label)
{
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException(label + " failed.");
    }
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static long GetDirectorySize(string path)
{
    if (!Directory.Exists(path)) return 0;
    long bytes = 0;
    foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
    {
        try { bytes += new FileInfo(file).Length; }
        catch (FileNotFoundException) { }
    }
    return bytes;
}

sealed record ProcessMetrics(
    long ManagedHeapBytes,
    long WorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double TotalProcessorMilliseconds = 0,
    long RootBytes = 0,
    int StagingFiles = 0,
    int DatabaseConnections = 0,
    long DatabaseBytes = 0,
    string PostgreSqlVersion = "")
{
    public static ProcessMetrics Max(ProcessMetrics left, ProcessMetrics right) => new(
        Math.Max(left.ManagedHeapBytes, right.ManagedHeapBytes),
        Math.Max(left.WorkingSetBytes, right.WorkingSetBytes),
        Math.Max(left.Gen0Collections, right.Gen0Collections),
        Math.Max(left.Gen1Collections, right.Gen1Collections),
        Math.Max(left.Gen2Collections, right.Gen2Collections),
        Math.Max(left.TotalProcessorMilliseconds, right.TotalProcessorMilliseconds),
        Math.Max(left.RootBytes, right.RootBytes),
        Math.Max(left.StagingFiles, right.StagingFiles),
        Math.Max(left.DatabaseConnections, right.DatabaseConnections),
        Math.Max(left.DatabaseBytes, right.DatabaseBytes),
        string.IsNullOrEmpty(right.PostgreSqlVersion) ? left.PostgreSqlVersion : right.PostgreSqlVersion);
}

sealed record TransferMeasurement(
    string Direction,
    long Bytes,
    int Concurrency,
    TimeSpan Duration,
    double ThroughputMiBPerSecond,
    ProcessMetrics ServerBaseline,
    ProcessMetrics ServerPeak,
    ProcessMetrics ClientBaseline,
    ProcessMetrics ClientPeak)
{
    public long ServerManagedHeapGrowth => ServerPeak.ManagedHeapBytes - ServerBaseline.ManagedHeapBytes;
    public long ServerWorkingSetGrowth => ServerPeak.WorkingSetBytes - ServerBaseline.WorkingSetBytes;
}

sealed record MemoryBudget(
    long ManagedHeapBaseBytes,
    long ManagedHeapPerTransferBytes,
    long WorkingSetBaseBytes,
    long WorkingSetPerTransferBytes);

sealed record HarnessReport(
    DateTimeOffset CompletedAt,
    string Runtime,
    string OperatingSystem,
    int CopyBufferBytes,
    int ClientMaxErrorRetry,
    string SamplingMethod,
    MemoryBudget AcceptanceBudget,
    bool CancelledOverwritePreserved,
    int StagingFilesAfterCancellation,
    IReadOnlyList<TransferMeasurement> Measurements);

sealed record HarnessOptions(
    long ComparisonBytes,
    long LargeBytes,
    int[] Concurrency,
    string WorkDirectory,
    string OutputPath)
{
    public int MaxConcurrency => Concurrency.Max();

    public static HarnessOptions Parse(string[] args)
    {
        Dictionary<string, string> values = args
            .Chunk(2)
            .Where(chunk => chunk.Length == 2)
            .ToDictionary(chunk => chunk[0], chunk => chunk[1], StringComparer.OrdinalIgnoreCase);
        long comparisonMiB = long.Parse(values.GetValueOrDefault("--comparison-mib", "64"));
        long largeMiB = long.Parse(values.GetValueOrDefault("--size-mib", "1024"));
        int[] concurrency = values.GetValueOrDefault("--concurrency", "1,4")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToArray();
        string work = Path.GetFullPath(values.GetValueOrDefault("--work", Path.GetTempPath()));
        string output = values.GetValueOrDefault(
            "--output",
            Path.Combine(Directory.GetCurrentDirectory(), "large-transfer-results.json"));
        if (comparisonMiB <= 0 || largeMiB <= comparisonMiB || concurrency.Any(value => value <= 0))
        {
            throw new ArgumentException("Require 0 < comparison-mib < size-mib and positive concurrency values.");
        }

        return new HarnessOptions(
            comparisonMiB * 1024 * 1024,
            largeMiB * 1024 * 1024,
            concurrency,
            work,
            output);
    }
}

sealed record SustainedOptions(
    TimeSpan Duration,
    int Concurrency,
    int ObjectBytes,
    string WorkDirectory,
    string OutputPath)
{
    public static SustainedOptions Parse(string[] args)
    {
        Dictionary<string, string> values = args.Chunk(2)
            .Where(chunk => chunk.Length == 2)
            .ToDictionary(chunk => chunk[0], chunk => chunk[1], StringComparer.OrdinalIgnoreCase);
        int durationSeconds = int.Parse(values.GetValueOrDefault("--duration-seconds", "60"));
        int concurrency = int.Parse(values.GetValueOrDefault("--concurrency", "4"));
        int objectKiB = int.Parse(values.GetValueOrDefault("--object-kib", "64"));
        string work = Path.GetFullPath(values.GetValueOrDefault("--work", Path.GetTempPath()));
        string output = values.GetValueOrDefault("--output", Path.Combine(Directory.GetCurrentDirectory(), "sustained-results.json"));
        if (durationSeconds <= 0 || concurrency <= 0 || objectKiB <= 0 || objectKiB > 16 * 1024)
            throw new ArgumentException("Require positive duration/concurrency and object-kib between 1 and 16384.");
        return new(TimeSpan.FromSeconds(durationSeconds), concurrency, checked(objectKiB * 1024), work, output);
    }
}

sealed record SustainedOperationSummary(
    string Operation,
    long Count,
    double MinimumMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds);

sealed record SustainedReport(
    DateTimeOffset CompletedAt,
    TimeSpan RequestedDuration,
    TimeSpan ActualDuration,
    int Concurrency,
    int ObjectBytes,
    long AttemptedOperations,
    long TotalOperations,
    double CompletedOperationsPerSecond,
    long ApproximateTransferredBytes,
    long VerifiedHashes,
    ProcessMetrics ServerStart,
    ProcessMetrics ServerEnd,
    double AverageServerCpuPercent,
    int FailureCount,
    IReadOnlyList<string> Failures,
    IReadOnlyList<SustainedOperationSummary> Operations);

sealed class ThrottledReadStream(Stream inner, TimeSpan delay) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        return await inner.ReadAsync(buffer, cancellationToken);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
