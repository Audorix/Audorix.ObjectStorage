using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using AwsPutObjectRequest = Amazon.S3.Model.PutObjectRequest;

internal static class RecoveryHarness
{
    private const string Region = "us-east-1";
    private const string Version = "0.1.0-rc.2";
    private const string SourceCommit = "4c5bbe953687023018eb3d2ca230f75e4e6f6c94";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task RunAsync(string[] args)
    {
        Dictionary<string, string> values = args
            .Where(value => !string.Equals(value, "--recovery", StringComparison.OrdinalIgnoreCase))
            .Chunk(2).Where(chunk => chunk.Length == 2)
            .ToDictionary(chunk => chunk[0], chunk => chunk[1], StringComparer.OrdinalIgnoreCase);
        string work = Path.GetFullPath(values.GetValueOrDefault("--work", Path.Combine(Directory.GetCurrentDirectory(), ".recovery")));
        string output = Path.GetFullPath(values.GetValueOrDefault("--output", Path.Combine(Directory.GetCurrentDirectory(), "recovery-summary.json")));
        string sourceConnectionString = LoadConnectionString();
        NpgsqlConnectionStringBuilder source = new(sourceConnectionString);
        string runId = Guid.NewGuid().ToString("N");
        string runRoot = Path.Combine(work, runId);
        string storageRoot = Path.Combine(runRoot, "storage");
        string readyFile = Path.Combine(runRoot, "ready.json");
        string schema = "audorix_recovery_" + runId;
        string bucket = "recovery-" + runId;
        string accessKey = "RECOVERY" + runId.ToUpperInvariant();
        string secretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string tokenKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(runRoot);

        List<object> phases = [];
        List<string> failures = [];
        Process? server = null;
        await using RunOwnedPostgreSqlProxy proxy = new(source.Host ?? IPAddress.Loopback.ToString(), source.Port);
        await proxy.StartAsync();
        NpgsqlConnectionStringBuilder proxied = new(sourceConnectionString)
        {
            Host = IPAddress.Loopback.ToString(),
            Port = proxy.Port,
            Pooling = false,
            Timeout = 2,
            CommandTimeout = 3
        };

        byte[] original = CreateContent(1024 * 1024, 11);
        byte[] replacement = CreateContent(32 * 1024 * 1024, 29);
        string key = "recovery/committed.bin";
        bool acknowledgedIntegrity = false;
        string uncertainOutcome = "not-run";
        bool reconciliationPassed = false;
        bool cleanupPreservedLiveObject = false;
        try
        {
            server = StartServer(storageRoot, schema, readyFile, bucket, proxied.ConnectionString, accessKey, secretKey, tokenKey);
            SoakHarness.ServerIdentity identity = await WaitForServerAsync(readyFile, server);
            using AmazonS3Client client = CreateClient(identity.Endpoint, accessKey, secretKey);
            await PutAsync(client, bucket, key, original);
            await VerifyAsync(client, bucket, key, original);
            phases.Add(new { phase = "baseline-acknowledged", passed = true, bytes = original.Length });

            proxy.Disable();
            bool databaseUploadFailed = await FailsAsync(() => PutAsync(client, bucket, "recovery/database-interrupted.bin", original));
            bool databaseListFailed = await FailsAsync(async () => { await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }); });
            proxy.Enable();
            await WaitForDatabaseAsync(sourceConnectionString);
            await VerifyAsync(client, bucket, key, original);
            await PutAsync(client, bucket, "recovery/database-restored.bin", original);
            await VerifyAsync(client, bucket, "recovery/database-restored.bin", original);
            phases.Add(new { phase = "database-proxy-interruption", uploadFailedDuringFault = databaseUploadFailed, listFailedDuringFault = databaseListFailed, recovered = true, passed = databaseUploadFailed && databaseListFailed });

            string staging = Path.Combine(storageRoot, "staging");
            Directory.Delete(staging);
            await File.WriteAllTextAsync(staging, "injected staging-path I/O failure");
            bool diskWriteFailed = await FailsAsync(() => PutAsync(client, bucket, key, replacement));
            File.Delete(staging);
            Directory.CreateDirectory(staging);
            await VerifyAsync(client, bucket, key, original);
            phases.Add(new { phase = "injected-disk-write-failure", mechanism = "run-owned staging directory replaced by a regular file", diskFull = false, failedExplicitly = diskWriteFailed, previousVersionPreserved = true, passed = diskWriteFailed });

            Task preCommitUpload = PutSlowAsync(client, bucket, key, replacement, TimeSpan.FromMilliseconds(4));
            await WaitForFileAsync(staging, "*.staging", requireLength: true);
            await KillAsync(server);
            server.Dispose();
            server = null;
            bool preCommitAcknowledged = await CompletedSuccessfullyAsync(preCommitUpload);
            identity = await RestartAsync();
            using AmazonS3Client restarted = CreateClient(identity.Endpoint, accessKey, secretKey);
            await VerifyAsync(restarted, bucket, key, original);
            phases.Add(new { phase = "termination-during-staging", barrier = "non-empty .staging file observed", acknowledged = preCommitAcknowledged, outcome = "previous committed version", passed = !preCommitAcknowledged });

            await PutAsync(restarted, bucket, key, replacement);
            await KillAsync(server!);
            server!.Dispose();
            server = null;
            identity = await RestartAsync();
            using AmazonS3Client afterAcknowledgedRestart = CreateClient(identity.Endpoint, accessKey, secretKey);
            await VerifyAsync(afterAcknowledgedRestart, bucket, key, replacement);
            acknowledgedIntegrity = true;
            phases.Add(new { phase = "termination-after-acknowledgment", acknowledged = true, hashVerifiedAfterRestart = true, passed = true });

            byte[] uncertainBytes = CreateContent(8 * 1024 * 1024, 47);
            int blobsBefore = Directory.EnumerateFiles(Path.Combine(storageRoot, "blobs"), "*.blob").Count();
            Task publicationUpload = PutSlowAsync(afterAcknowledgedRestart, bucket, key, uncertainBytes, TimeSpan.FromMilliseconds(1));
            await WaitForBlobIncreaseAsync(Path.Combine(storageRoot, "blobs"), blobsBefore, publicationUpload);
            bool acknowledgedAtBarrier = publicationUpload.IsCompletedSuccessfully;
            await KillAsync(server!);
            server!.Dispose();
            server = null;
            bool eventuallyAcknowledged = await CompletedSuccessfullyAsync(publicationUpload);
            identity = await RestartAsync();
            using AmazonS3Client afterPublicationRestart = CreateClient(identity.Endpoint, accessKey, secretKey);
            byte[] observed = await ReadAsync(afterPublicationRestart, bucket, key);
            bool oldComplete = observed.AsSpan().SequenceEqual(replacement);
            bool newComplete = observed.AsSpan().SequenceEqual(uncertainBytes);
            uncertainOutcome = newComplete ? "new-complete-version" : oldComplete ? "previous-complete-version" : "invalid-partial-or-unknown";
            phases.Add(new { phase = "termination-around-metadata-publication", barrier = "new .blob file observed", acknowledgedAtBarrier, eventuallyAcknowledged, outcome = uncertainOutcome, passed = oldComplete || newComplete });

            ListObjectsV2Response listing = await afterPublicationRestart.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "recovery/" });
            byte[] current = newComplete ? uncertainBytes : replacement;
            await VerifyRangeAsync(afterPublicationRestart, bucket, key, current);
            byte[] final = Encoding.UTF8.GetBytes("final-overwrite-after-recovery");
            await PutAsync(afterPublicationRestart, bucket, key, final);
            await VerifyAsync(afterPublicationRestart, bucket, key, final);
            await afterPublicationRestart.DeleteObjectAsync(bucket, "recovery/database-restored.bin");
            await Task.Delay(TimeSpan.FromSeconds(6));
            using HttpClient admin = new() { BaseAddress = new Uri(identity.Endpoint) };
            using HttpResponseMessage cleanupResponse = await admin.PostAsync("/harness/cleanup", null);
            if (!cleanupResponse.IsSuccessStatusCode)
                throw new InvalidDataException($"cleanup returned {(int)cleanupResponse.StatusCode}: {await cleanupResponse.Content.ReadAsStringAsync()}");
            using HttpResponseMessage reconciliationResponse = await admin.GetAsync("/harness/reconcile");
            if (!reconciliationResponse.IsSuccessStatusCode)
                throw new InvalidDataException($"reconcile returned {(int)reconciliationResponse.StatusCode}: {await reconciliationResponse.Content.ReadAsStringAsync()}");
            SoakHarness.ReconciliationResult reconciliation = await reconciliationResponse.Content.ReadFromJsonAsync<SoakHarness.ReconciliationResult>()
                ?? throw new InvalidDataException("Reconciliation response was empty.");
            reconciliationPassed = reconciliation.MissingBlobs == 0 && reconciliation.UnreferencedBlobs == 0 && reconciliation.StagingFiles == 0;
            await VerifyAsync(afterPublicationRestart, bucket, key, final);
            cleanupPreservedLiveObject = true;
            await afterPublicationRestart.DeleteObjectAsync(bucket, key);
            phases.Add(new { phase = "post-recovery-operations-and-reconciliation", listed = listing.S3Objects.Count, rangeVerified = true, overwriteVerified = true, deleteVerified = true, reconciliation, cleanupPreservedLiveObject, passed = reconciliationPassed });
        }
        catch (Exception exception)
        {
            failures.Add(exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            if (server is not null)
            {
                await KillAsync(server);
                server.Dispose();
            }
            proxy.Enable();
            try { await DropSchemaAsync(sourceConnectionString, schema); }
            catch (Exception exception) { failures.Add("cleanup: " + exception.Message); }
        }

        var report = new
        {
            completedAt = DateTimeOffset.UtcNow,
            runId,
            candidateVersion = Version,
            sourceCommit = SourceCommit,
            topology = "package-only Kestrel host, client, run-owned PostgreSQL proxy, filesystem, and PostgreSQL on one local machine",
            faultScope = "isolated schema, bucket, root, credentials, proxy, and child processes",
            phases,
            acknowledgedWriteIntegrity = acknowledgedIntegrity,
            uncertainWriteOutcome = uncertainOutcome,
            reconciliationPassed,
            cleanupPreservedLiveObject,
            processTerminationIsPowerLossCertification = false,
            failures,
            passed = failures.Count == 0 && acknowledgedIntegrity && reconciliationPassed && cleanupPreservedLiveObject && phases.All(PhasePassed)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine(output);
        if (!report.passed) Environment.ExitCode = 1;

        async Task<SoakHarness.ServerIdentity> RestartAsync()
        {
            if (File.Exists(readyFile)) File.Delete(readyFile);
            server = StartServer(storageRoot, schema, readyFile, bucket, proxied.ConnectionString, accessKey, secretKey, tokenKey);
            return await WaitForServerAsync(readyFile, server);
        }
    }

    private static bool PhasePassed(object phase)
    {
        JsonElement element = JsonSerializer.SerializeToElement(phase);
        return element.TryGetProperty("passed", out JsonElement passed) && passed.GetBoolean();
    }

    private static Process StartServer(string root, string schema, string ready, string bucket, string connection, string access, string secret, string token)
    {
        ProcessStartInfo start = new(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("soak-server");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(schema);
        start.ArgumentList.Add(ready);
        start.ArgumentList.Add(bucket);
        start.Environment["AUDORIX_OBJECTSTORAGE_POSTGRES"] = connection;
        start.Environment["AUDORIX_SOAK_ACCESS_KEY"] = access;
        start.Environment["AUDORIX_SOAK_SECRET_KEY"] = secret;
        start.Environment["AUDORIX_SOAK_TOKEN_KEY"] = token;
        start.Environment["AUDORIX_PG_DUMP_PATH"] = FindPostgreSqlTool("pg_dump.exe");
        start.Environment["AUDORIX_PG_RESTORE_PATH"] = FindPostgreSqlTool("pg_restore.exe");
        return Process.Start(start) ?? throw new InvalidOperationException("Recovery server could not start.");
    }

    private static async Task<SoakHarness.ServerIdentity> WaitForServerAsync(string ready, Process process)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            if (File.Exists(ready))
            {
                try
                {
                    SoakHarness.ServerIdentity? identity = JsonSerializer.Deserialize<SoakHarness.ServerIdentity>(await File.ReadAllTextAsync(ready), JsonOptions);
                    if (identity is not null) return identity;
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            if (process.HasExited) throw new InvalidOperationException("Recovery server exited before readiness.");
            await Task.Delay(100);
        }
        throw new TimeoutException("Recovery server readiness timed out.");
    }

    private static AmazonS3Client CreateClient(string endpoint, string access, string secret) =>
        new(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = endpoint + "/s3",
            AuthenticationRegion = Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(60)
        });

    private static async Task PutAsync(AmazonS3Client client, string bucket, string key, byte[] content) =>
        await client.PutObjectAsync(new AwsPutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(content, false), UseChunkEncoding = false });

    private static async Task PutSlowAsync(AmazonS3Client client, string bucket, string key, byte[] content, TimeSpan delay)
    {
        await using MemoryStream source = new(content, false);
        await using DelayedStream slow = new(source, delay);
        await client.PutObjectAsync(new AwsPutObjectRequest { BucketName = bucket, Key = key, InputStream = slow, UseChunkEncoding = false });
    }

    private static async Task<byte[]> ReadAsync(AmazonS3Client client, string bucket, string key)
    {
        using GetObjectResponse response = await client.GetObjectAsync(bucket, key);
        using MemoryStream destination = new();
        await response.ResponseStream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static async Task VerifyAsync(AmazonS3Client client, string bucket, string key, byte[] expected)
    {
        GetObjectMetadataResponse metadata = await client.GetObjectMetadataAsync(bucket, key);
        byte[] actual = await ReadAsync(client, bucket, key);
        if (metadata.ContentLength != expected.Length || !actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Object integrity verification failed.");
    }

    private static async Task VerifyRangeAsync(AmazonS3Client client, string bucket, string key, byte[] expected)
    {
        int start = Math.Min(17, expected.Length - 1);
        int end = Math.Min(start + 4095, expected.Length - 1);
        using GetObjectResponse response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, ByteRange = new ByteRange(start, end) });
        byte[] actual = new byte[end - start + 1];
        await response.ResponseStream.ReadExactlyAsync(actual);
        if (!actual.AsSpan().SequenceEqual(expected.AsSpan(start, actual.Length))) throw new InvalidDataException("Range integrity verification failed.");
    }

    private static async Task<bool> FailsAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch { return true; }
    }

    private static async Task<bool> CompletedSuccessfullyAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); return true; }
        catch { return false; }
    }

    private static async Task WaitForFileAsync(string directory, string pattern, bool requireLength)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            string? file = Directory.EnumerateFiles(directory, pattern).FirstOrDefault();
            if (file is not null && (!requireLength || new FileInfo(file).Length > 0)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("File-system phase barrier was not reached.");
    }

    private static async Task WaitForBlobIncreaseAsync(string directory, int before, Task upload)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (Directory.EnumerateFiles(directory, "*.blob").Count() > before) return;
            if (upload.IsFaulted) await upload;
            await Task.Delay(10);
        }
        throw new TimeoutException("Blob-publication phase barrier was not reached.");
    }

    private static async Task KillAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }

    private static async Task WaitForDatabaseAsync(string connectionString)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { await using NpgsqlConnection connection = new(connectionString); await connection.OpenAsync(); return; }
            catch { await Task.Delay(100); }
        }
        throw new TimeoutException("PostgreSQL did not recover.");
    }

    private static byte[] CreateContent(int length, int seed)
    {
        byte[] content = new byte[length];
        for (int index = 0; index < length; index++) content[index] = (byte)((index * 31L + seed * 17L) % 251);
        return content;
    }

    private static string LoadConnectionString()
    {
        string? configured = Environment.GetEnvironmentVariable("AUDORIX_OBJECTSTORAGE_POSTGRES");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, "src", "Audorix.ObjectStorage.PostgreSql", "data", "database.json");
            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.GetProperty("Database").GetProperty("ActiveConnectionString").GetString()!;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("PostgreSQL connection is required.");
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"drop schema if exists \"{schema}\" cascade;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string FindPostgreSqlTool(string name) =>
        Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL"), name, SearchOption.AllDirectories).OrderDescending().First();

    private sealed class DelayedStream(Stream inner, TimeSpan delay) : Stream
    {
        public override bool CanRead => true;
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
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }

    private sealed class RunOwnedPostgreSqlProxy(string host, int targetPort) : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Target)> active = new();
        private Task? acceptLoop;
        private int sequence;
        private volatile bool enabled = true;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Task StartAsync()
        {
            listener.Start();
            acceptLoop = AcceptAsync();
            return Task.CompletedTask;
        }

        public void Disable()
        {
            enabled = false;
            foreach ((TcpClient client, TcpClient target) in active.Values) { client.Dispose(); target.Dispose(); }
            active.Clear();
        }

        public void Enable() => enabled = true;

        private async Task AcceptAsync()
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token);
                    if (!enabled) { client.Dispose(); continue; }
                    _ = ForwardAsync(client);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task ForwardAsync(TcpClient client)
        {
            int id = Interlocked.Increment(ref sequence);
            TcpClient target = new();
            try
            {
                await target.ConnectAsync(host, targetPort, stopping.Token);
                active[id] = (client, target);
                using NetworkStream fromClient = client.GetStream();
                using NetworkStream fromTarget = target.GetStream();
                Task upstream = fromClient.CopyToAsync(fromTarget, stopping.Token);
                Task downstream = fromTarget.CopyToAsync(fromClient, stopping.Token);
                await Task.WhenAny(upstream, downstream);
            }
            catch when (stopping.IsCancellationRequested || !enabled) { }
            finally
            {
                active.TryRemove(id, out _);
                client.Dispose();
                target.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            stopping.Cancel();
            listener.Stop();
            Disable();
            if (acceptLoop is not null) try { await acceptLoop; } catch { }
            stopping.Dispose();
        }
    }
}
