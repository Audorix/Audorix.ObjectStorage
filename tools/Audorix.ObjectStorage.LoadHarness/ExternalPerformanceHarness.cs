using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

internal static class ExternalPerformanceHarness
{
    public static async Task RunAsync(string[] args)
    {
        Options options = Options.Parse(args);
        string access = RequiredEnvironment("AUDORIX_EXTERNAL_ACCESS_KEY");
        string secret = RequiredEnvironment("AUDORIX_EXTERNAL_SECRET_KEY");
        string opsToken = RequiredEnvironment("AUDORIX_EXTERNAL_OPERATIONS_TOKEN");
        Environment.SetEnvironmentVariable("AWS_REQUEST_CHECKSUM_CALCULATION", "WHEN_REQUIRED");
        Environment.SetEnvironmentVariable("AWS_RESPONSE_CHECKSUM_VALIDATION", "WHEN_REQUIRED");
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        using AmazonS3Client client = new(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = options.Endpoint.TrimEnd('/') + "/s3",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromMinutes(5),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
        if (!string.IsNullOrWhiteSpace(options.ForwardedHost))
        {
            client.BeforeRequestEvent += (_, eventArgs) =>
            {
                if (eventArgs is WebServiceRequestEventArgs request) request.Headers["X-Forwarded-Host"] = options.ForwardedHost;
            };
        }
        using HttpClient metrics = new() { BaseAddress = new Uri(options.Endpoint) };
        metrics.DefaultRequestHeaders.Authorization = new("Bearer", opsToken);
        Process process = Process.GetCurrentProcess();
        ResourceSnapshot before = await SnapshotAsync(metrics, process);
        Dictionary<string, List<double>> samples = new(StringComparer.Ordinal);
        List<string> errors = [];
        long uploaded = 0, downloaded = 0;
        string prefix = $"fifteenth/perf-{Guid.NewGuid():N}/";
        byte[] small = CreateContent(options.SmallBytes, 17);
        byte[] large = CreateContent(options.LargeBytes, 41);

        async Task Measure(string operation, Func<Task> action)
        {
            Stopwatch timer = Stopwatch.StartNew();
            try { await action(); }
            catch (Exception exception) { errors.Add(operation + ": " + exception.GetType().Name); return; }
            timer.Stop();
            if (!samples.TryGetValue(operation, out List<double>? values)) samples[operation] = values = [];
            values.Add(timer.Elapsed.TotalMilliseconds);
        }

        for (int i = 0; i < options.Warmup; i++)
        {
            string key = prefix + $"warmup-{i:D3}";
            await PutAsync(client, options.Bucket, key, small);
            using GetObjectResponse response = await client.GetObjectAsync(options.Bucket, key);
            await response.ResponseStream.CopyToAsync(Stream.Null);
            await client.DeleteObjectAsync(options.Bucket, key);
        }

        Stopwatch measurementWall = Stopwatch.StartNew();
        for (int i = 0; i < options.Samples; i++)
        {
            string key = prefix + $"small-{i:D4}";
            await Measure("small-put", async () => { await PutAsync(client, options.Bucket, key, small); uploaded += small.Length; });
            await Measure("small-head", async () => { GetObjectMetadataResponse head = await client.GetObjectMetadataAsync(options.Bucket, key); if (head.ContentLength != small.Length) throw new InvalidDataException(); });
            Stopwatch totalGet = Stopwatch.StartNew();
            Stopwatch headers = Stopwatch.StartNew();
            GetObjectResponse? get = null;
            try { get = await client.GetObjectAsync(options.Bucket, key); headers.Stop(); Add(samples, "small-get-headers", headers.Elapsed.TotalMilliseconds); }
            catch (Exception exception) { errors.Add("small-get: " + exception.GetType().Name); }
            if (get is not null)
            {
                using (get)
                {
                    await Measure("small-get-body", async () => { byte[] actual = await ReadAllAsync(get.ResponseStream); if (!actual.AsSpan().SequenceEqual(small)) throw new InvalidDataException(); downloaded += actual.Length; });
                }
                totalGet.Stop(); Add(samples, "small-get-end-to-end", totalGet.Elapsed.TotalMilliseconds);
            }
            await Measure("range-4k", async () => { using GetObjectResponse range = await client.GetObjectAsync(new GetObjectRequest { BucketName = options.Bucket, Key = key, ByteRange = new ByteRange(0, Math.Min(4095, small.Length - 1)) }); byte[] actual = await ReadAllAsync(range.ResponseStream); if (!actual.AsSpan().SequenceEqual(small.AsSpan(0, actual.Length))) throw new InvalidDataException(); downloaded += actual.Length; });
            await Measure("small-delete", async () => { await client.DeleteObjectAsync(options.Bucket, key); });
        }

        for (int i = 0; i < options.ListObjects; i++) await PutAsync(client, options.Bucket, prefix + $"list-{i:D5}", small.AsMemory(0, Math.Min(1024, small.Length)).ToArray());
        for (int pass = 0; pass < options.ListPasses; pass++)
        {
            string? token = null;
            do
            {
                ListObjectsV2Response? page = null;
                await Measure("list-page", async () => page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = options.Bucket, Prefix = prefix + "list-", MaxKeys = options.ListPageSize, ContinuationToken = token }));
                token = page?.IsTruncated == true ? page.NextContinuationToken : null;
            } while (token is not null);
        }

        string largeKey = prefix + "large-stream.bin";
        for (int i = 0; i < options.LargeSamples; i++)
        {
            await Measure("large-put-complete", async () => { await PutAsync(client, options.Bucket, largeKey, large); uploaded += large.Length; });
            Stopwatch totalGet = Stopwatch.StartNew();
            Stopwatch headers = Stopwatch.StartNew();
            using GetObjectResponse response = await client.GetObjectAsync(options.Bucket, largeKey);
            headers.Stop(); Add(samples, "large-get-headers", headers.Elapsed.TotalMilliseconds);
            await Measure("large-get-body", async () => { byte[] actual = await ReadAllAsync(response.ResponseStream); if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(large))) throw new InvalidDataException(); downloaded += actual.Length; });
            totalGet.Stop(); Add(samples, "large-get-end-to-end", totalGet.Elapsed.TotalMilliseconds);
        }
        measurementWall.Stop();
        await client.DeleteObjectAsync(options.Bucket, largeKey);
        await DeletePrefixAsync(client, options.Bucket, prefix);
        ResourceSnapshot after = await SnapshotAsync(metrics, process);
        var summaries = samples.OrderBy(item => item.Key).Select(item => Summary.Create(item.Key, item.Value)).ToArray();
        int listPagesPerPass = (int)Math.Ceiling((double)options.ListObjects / options.ListPageSize);
        int measuredNetworkRequests = (options.Samples * 5) + (options.ListPasses * listPagesPerPass) + (options.LargeSamples * 2);
        double uploadSeconds = summaries.Where(s => s.Operation is "small-put" or "large-put-complete").Sum(s => s.TotalMilliseconds) / 1000d;
        double downloadSeconds = summaries.Where(s => s.Operation is "small-get-end-to-end" or "large-get-end-to-end" or "range-4k").Sum(s => s.TotalMilliseconds) / 1000d;
        var report = new
        {
            recordedAtUtc = DateTimeOffset.UtcNow,
            options,
            sdk = new { name = "AWSSDK.S3", version = typeof(AmazonS3Client).Assembly.GetName().Version?.ToString(), persistentClients = 1, maxErrorRetry = 0, requestChecksumCalculation = "WHEN_REQUIRED", responseChecksumValidation = "WHEN_REQUIRED" },
            semantics = new { headers = "time until SDK returns response headers", body = "time to consume and integrity-check the response body after headers", endToEnd = "time from SDK invocation through complete verified body", measuredNetworkRequests = "requests with operation-level timing; excludes warm-up, list-fixture creation, and cleanup", forwardedHostProbe = options.ForwardedHost },
            totals = new { uploadedBytes = uploaded, downloadedBytes = downloaded, errors = errors.Count, retries = 0, measuredNetworkRequests, measuredTimingSamples = summaries.Sum(s => s.Count), warmupNetworkRequests = options.Warmup * 3, listFixturePutRequests = options.ListObjects },
            throughput = new { measurementWallSeconds = measurementWall.Elapsed.TotalSeconds, uploadMiBPerSecond = uploadSeconds == 0 ? 0 : uploaded / 1048576d / uploadSeconds, downloadMiBPerSecond = downloadSeconds == 0 ? 0 : downloaded / 1048576d / downloadSeconds },
            resources = new { before, after, clientCpuMilliseconds = after.ClientCpuMilliseconds - before.ClientCpuMilliseconds },
            operations = summaries,
            errorTypes = errors.GroupBy(e => e).ToDictionary(g => g.Key, g => g.Count())
        };
        await File.WriteAllTextAsync(options.Output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (errors.Count > 0) throw new InvalidOperationException($"Focused measurement recorded {errors.Count} errors.");
    }

    private static async Task PutAsync(AmazonS3Client client, string bucket, string key, byte[] content) =>
        await client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(content, writable: false), AutoCloseStream = true, UseChunkEncoding = false });
    private static async Task<byte[]> ReadAllAsync(Stream stream) { using MemoryStream memory = new(); await stream.CopyToAsync(memory); return memory.ToArray(); }
    private static byte[] CreateContent(int bytes, int seed) { byte[] content = new byte[bytes]; new Random(seed).NextBytes(content); return content; }
    private static void Add(Dictionary<string, List<double>> values, string key, double value) { if (!values.TryGetValue(key, out List<double>? list)) values[key] = list = []; list.Add(value); }
    private static async Task DeletePrefixAsync(AmazonS3Client client, string bucket, string prefix)
    {
        string? token = null;
        do { ListObjectsV2Response page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, ContinuationToken = token }); foreach (S3Object item in page.S3Objects) await client.DeleteObjectAsync(bucket, item.Key); token = page.IsTruncated == true ? page.NextContinuationToken : null; } while (token is not null);
    }
    private static async Task<ResourceSnapshot> SnapshotAsync(HttpClient client, Process process)
    {
        JsonElement json = await client.GetFromJsonAsync<JsonElement>("/ops/metrics"); process.Refresh();
        return new(DateTimeOffset.UtcNow, process.WorkingSet64, GC.GetTotalMemory(false), process.TotalProcessorTime.TotalMilliseconds,
            json.GetProperty("workingSetBytes").GetInt64(), json.GetProperty("managedHeapBytes").GetInt64(), json.GetProperty("freeDiskBytes").GetInt64(), json.GetProperty("stagingFiles").GetInt32());
    }
    private static string RequiredEnvironment(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : throw new InvalidOperationException(key + " is required.");
    private sealed record ResourceSnapshot(DateTimeOffset AtUtc, long ClientWorkingSetBytes, long ClientManagedHeapBytes, double ClientCpuMilliseconds, long ServerWorkingSetBytes, long ServerManagedHeapBytes, long ServerFreeDiskBytes, int ServerStagingFiles);
    private sealed record Summary(string Operation, int Count, double MinimumMilliseconds, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaximumMilliseconds, double AverageMilliseconds, double TotalMilliseconds)
    {
        public static Summary Create(string operation, List<double> values) { double[] s = values.Order().ToArray(); return new(operation, s.Length, s[0], P(s,.5), P(s,.95), P(s,.99), s[^1], s.Average(), s.Sum()); }
        private static double P(double[] s, double p) => s[Math.Clamp((int)Math.Ceiling(p * s.Length) - 1, 0, s.Length - 1)];
    }
    private sealed record Options(string Endpoint, string Bucket, int Warmup, int Samples, int SmallBytes, int ListObjects, int ListPageSize, int ListPasses, int LargeBytes, int LargeSamples, int Concurrency, string? ForwardedHost, string Output)
    {
        public static Options Parse(string[] args) { Dictionary<string,string> v=args.Chunk(2).Where(c=>c.Length==2).ToDictionary(c=>c[0],c=>c[1],StringComparer.OrdinalIgnoreCase); return new(v["--endpoint"],v["--bucket"],I(v,"--warmup",20),I(v,"--samples",200),I(v,"--small-bytes",65536),I(v,"--list-objects",250),I(v,"--list-page-size",50),I(v,"--list-passes",20),I(v,"--large-bytes",8388608),I(v,"--large-samples",30),1,v.GetValueOrDefault("--forwarded-host"),Path.GetFullPath(v["--output"])); }
        private static int I(Dictionary<string,string> v,string k,int d)=>int.Parse(v.GetValueOrDefault(k,d.ToString()));
    }
}
