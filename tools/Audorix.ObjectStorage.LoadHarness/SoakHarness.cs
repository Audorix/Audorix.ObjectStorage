using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Audorix.ObjectStorage;
using Audorix.ObjectStorage.DependencyInjection;
using Audorix.ObjectStorage.Metadata;
using Audorix.ObjectStorage.PostgreSql;
using Audorix.ObjectStorage.PostgreSql.DependencyInjection;
using Audorix.ObjectStorage.S3;
using Audorix.ObjectStorage.S3.DependencyInjection;
using Audorix.ObjectStorage.S3.Routing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Npgsql;
using AwsPutObjectRequest = Amazon.S3.Model.PutObjectRequest;

internal static class SoakHarness
{
    private const string Region = "us-east-1";
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task RunServerAsync(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("soak-server requires root, schema, ready file, and bucket.");
        string root = Path.GetFullPath(args[0]);
        string schema = args[1];
        string readyFile = Path.GetFullPath(args[2]);
        string bucket = args[3];
        string connectionString = RequiredEnvironment("AUDORIX_OBJECTSTORAGE_POSTGRES");
        string accessKey = RequiredEnvironment("AUDORIX_SOAK_ACCESS_KEY");
        string secretKey = RequiredEnvironment("AUDORIX_SOAK_SECRET_KEY");
        string tokenKey = RequiredEnvironment("AUDORIX_SOAK_TOKEN_KEY");
        string pgDump = RequiredEnvironment("AUDORIX_PG_DUMP_PATH");
        string pgRestore = RequiredEnvironment("AUDORIX_PG_RESTORE_PATH");

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAudorixObjectStorage(options =>
        {
            options.StorageRootPath = root;
            options.CopyBufferSize = BufferSize;
            options.UnreferencedBlobRetention = TimeSpan.FromSeconds(5);
        });
        builder.Services.AddAudorixObjectStoragePostgreSql(options =>
        {
            options.ConnectionString = connectionString;
            options.SchemaName = schema;
        });
        builder.Services.AddAudorixObjectStoragePostgreSqlBackup(options =>
        {
            options.PgDumpPath = pgDump;
            options.PgRestorePath = pgRestore;
        });
        builder.Services.AddAudorixObjectStorageS3(options =>
        {
            options.Region = Region;
            options.MaxObjectSize = 512L * 1024 * 1024;
            options.MaxConcurrentOperations = 32;
            options.MaxQueuedOperations = 8;
            options.ContinuationTokenSigningKey = tokenKey;
            options.Credentials.Add(new S3Credential
            {
                AccessKeyId = accessKey,
                SecretAccessKey = secretKey,
                AllowedBuckets = [bucket],
                AllowedOperations = S3Operation.AllObjectOperations | S3Operation.ListBucket
            });
        });

        WebApplication app = builder.Build();
        app.MapAudorixObjectStorageS3("/s3");
        app.MapGet("/harness/metrics", async (CancellationToken cancellationToken) =>
        {
            using Process process = Process.GetCurrentProcess();
            long rootBytes = GetDirectorySize(root);
            int stagingFiles = Directory.Exists(Path.Combine(root, "staging"))
                ? Directory.EnumerateFiles(Path.Combine(root, "staging"), "*.staging").Count()
                : 0;
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand command = new(
                "select count(*)::int, current_setting('max_connections')::int, pg_database_size(current_database());",
                connection);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return Results.Json(new SoakProcessMetrics(
                DateTimeOffset.UtcNow,
                GC.GetTotalMemory(false),
                process.WorkingSet64,
                process.TotalProcessorTime.TotalMilliseconds,
                rootBytes,
                stagingFiles,
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        });
        app.MapGet("/harness/reconcile", async (IObjectStorageMetadataStore metadata, CancellationToken cancellationToken) =>
        {
            IReadOnlySet<string> referenced = await metadata.GetReferencedBlobIdsAsync(cancellationToken);
            HashSet<string> physical = Directory.Exists(Path.Combine(root, "blobs"))
                ? Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*.blob")
                    .Select(path => Path.GetFileNameWithoutExtension(path)!).ToHashSet(StringComparer.Ordinal)
                : [];
            int staging = Directory.Exists(Path.Combine(root, "staging"))
                ? Directory.EnumerateFiles(Path.Combine(root, "staging"), "*.staging").Count()
                : 0;
            return Results.Json(new ReconciliationResult(
                referenced.Count,
                physical.Count,
                referenced.Count(id => !physical.Contains(id)),
                physical.Count(id => !referenced.Contains(id)),
                staging));
        });
        app.MapPost("/harness/cleanup", async (IObjectStorageEngine storage, CancellationToken cancellationToken) =>
            Results.Json(new { removed = await storage.CleanupUnreferencedBlobsAsync(cancellationToken) }));
        app.MapPost("/harness/backup", async (
            PathRequest request,
            PostgreSqlObjectStorageBackupService backup,
            CancellationToken cancellationToken) =>
        {
            ObjectStorageBackupManifest manifest = await backup.CreateAsync(request.Path, cancellationToken);
            BackupValidationResult validation = await backup.ValidateAsync(request.Path, cancellationToken);
            return Results.Json(new BackupEndpointResult(manifest.ObjectCount, validation.ValidatedObjectCount, manifest.BackupSetId));
        });
        app.MapPost("/harness/validate-backup", async (
            PathRequest request,
            PostgreSqlObjectStorageBackupService backup,
            CancellationToken cancellationToken) =>
        {
            try
            {
                BackupValidationResult validation = await backup.ValidateAsync(request.Path, cancellationToken);
                return Results.Json(new BackupValidationEndpointResult(true, validation.ValidatedObjectCount, null));
            }
            catch (BackupValidationException exception)
            {
                return Results.Json(new BackupValidationEndpointResult(false, 0, exception.GetType().Name));
            }
        });
        app.MapPost("/harness/restore", async (
            RestoreRequest request,
            PostgreSqlObjectStorageBackupService backup,
            CancellationToken cancellationToken) =>
        {
            await backup.RestoreAsync(request.BackupPath, request.StorageRoot, request.TargetConnectionString, cancellationToken);
            return Results.Ok();
        });
        app.MapPost("/harness/stop", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Ok();
        });

        await app.StartAsync();
        IObjectStorageEngine storage = app.Services.GetRequiredService<IObjectStorageEngine>();
        await storage.CreateBucketAsync(bucket);
        string endpoint = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using Process current = Process.GetCurrentProcess();
        string executable = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        ServerIdentity identity = new(
            endpoint,
            current.Id,
            current.StartTime.ToString("O"),
            executable,
            await HashFileAsync(executable));
        await WriteJsonAtomicAsync(readyFile, identity);
        await app.WaitForShutdownAsync();
    }

    public static async Task RunAsync(string[] args)
    {
        SoakOptions options = SoakOptions.Parse(args);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        string connectionString = LoadConnectionString();
        NpgsqlConnectionStringBuilder sourceConnection = new(connectionString);
        Directory.CreateDirectory(options.RunDirectory);
        string root = Path.Combine(options.RunDirectory, "source-root");
        string restoredRoot = Path.Combine(options.RunDirectory, "restored-root");
        string backupPath = Path.Combine(options.RunDirectory, "backup-set");
        string inconsistentPath = Path.Combine(options.RunDirectory, "inconsistent-backup");
        string readyFile = Path.Combine(options.RunDirectory, "host-ready.json");
        string restoredReadyFile = Path.Combine(options.RunDirectory, "restored-host-ready.json");
        string runToken = options.RunId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        string schema = "audorix_soak_" + runToken;
        string bucket = "audorix-soak-" + runToken;
        string restoreDatabase = "audorix_restore_" + runToken;
        string accessKey = "AUDORIXSOAK" + runToken.ToUpperInvariant();
        string secretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string tokenKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string pgDump = FindPostgreSqlTool("pg_dump.exe");
        string pgRestore = FindPostgreSqlTool("pg_restore.exe");
        DriveInfo drive = new(Path.GetPathRoot(options.RunDirectory)!);
        if (drive.AvailableFreeSpace < options.DiskBudgetBytes + 2L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Available disk space is below the configured budget plus the 2 GiB safety reserve.");

        AcceptanceBudgets budgets = new(
            UnexpectedFailures: 0,
            Retries: 0,
            DataOperationP95Milliseconds: 2000,
            CleanupP95Milliseconds: 10000,
            ControlledCancellationP95Milliseconds: 5000,
            ManagedHeapBytes: 512L * 1024 * 1024,
            WorkingSetBytes: 1024L * 1024 * 1024,
            RunDiskBytes: options.DiskBudgetBytes,
            PostgreSqlConnections: 32,
            ResidualStagingFiles: 0,
            MissingOrUnreferencedBlobs: 0);
        EnvironmentEvidence environment = await CaptureEnvironmentAsync(sourceConnection, drive, pgRestore);
        IReadOnlyList<AssemblyEvidence> assemblies = await CaptureAssemblyEvidenceAsync();
        IReadOnlyList<CandidatePackageEvidence> candidatePackages = await CaptureCandidatePackagesAsync();
        SoakCounters counters = new();
        List<SoakSample> samples = [];
        List<RetainedObject> retained = [];
        Process? sourceServer = null;
        Process? restoredServer = null;
        ServerIdentity? sourceIdentity = null;
        ServerIdentity? restartedIdentity = null;
        ServerIdentity? restoredIdentity = null;
        ReconciliationResult? sourceReconciliation = null;
        ReconciliationResult? restoredReconciliation = null;
        BackupEndpointResult? backupResult = null;
        bool inconsistentRejected = false;
        bool restartVerified = false;
        bool restoreVerified = false;
        bool originalVerifiedAfterRestore = false;
        bool cleanedOwnedResources = false;
        string? terminalError = null;
        Stopwatch workload = new();

        await WriteCheckpointAsync("preparing", null);
        try
        {
            Directory.CreateDirectory(root);
            sourceServer = StartServer(root, schema, readyFile, bucket, connectionString, accessKey, secretKey, tokenKey, pgDump, pgRestore);
            sourceIdentity = await WaitForServerAsync(readyFile, sourceServer);
            await WriteCheckpointAsync("seeding", sourceIdentity);
            using AmazonS3Client sourceClient = CreateClient(sourceIdentity.Endpoint, accessKey, secretKey);
            retained.AddRange(await SeedRetainedAsync(sourceClient, bucket, options));

            using CancellationTokenSource workloadCancellation = new(options.Duration);
            Task sampler = SampleAsync(sourceIdentity.Endpoint, workloadCancellation.Token);
            Task cleanup = CleanupAsync(sourceIdentity.Endpoint, workloadCancellation.Token);
            workload.Start();
            Task[] workers = Enumerable.Range(0, options.Concurrency)
                .Select(worker => WorkAsync(worker, sourceIdentity.Endpoint, workloadCancellation.Token))
                .ToArray();
            await Task.WhenAll(workers);
            workload.Stop();
            workloadCancellation.Cancel();
            try { await sampler; } catch (OperationCanceledException) { }
            try { await cleanup; } catch (OperationCanceledException) { }
            await WriteCheckpointAsync("draining", sourceIdentity);

            await DeletePrefixAsync(sourceClient, bucket, "transient/");
            await DeletePrefixAsync(sourceClient, bucket, "cancel/");
            await VerifyRetainedAsync(sourceClient, bucket, retained, counters);
            await Task.Delay(TimeSpan.FromSeconds(6));
            using HttpClient admin = new() { BaseAddress = new Uri(sourceIdentity.Endpoint) };
            (await admin.PostAsync("/harness/cleanup", null)).EnsureSuccessStatusCode();
            sourceReconciliation = await admin.GetFromJsonAsync<ReconciliationResult>("/harness/reconcile")
                ?? throw new InvalidDataException("Source reconciliation response was empty.");
            EnsureReconciled(sourceReconciliation, "source");

            await StopServerAsync(sourceServer, sourceIdentity.Endpoint);
            sourceServer.Dispose();
            sourceServer = StartServer(root, schema, readyFile, bucket, connectionString, accessKey, secretKey, tokenKey, pgDump, pgRestore);
            restartedIdentity = await WaitForServerAsync(readyFile, sourceServer);
            using AmazonS3Client restartedClient = CreateClient(restartedIdentity.Endpoint, accessKey, secretKey);
            await VerifyRetainedAsync(restartedClient, bucket, retained, counters);
            await VerifyDeniedAsync(restartedIdentity.Endpoint, bucket, counters);
            restartVerified = true;
            await WriteCheckpointAsync("backing-up", restartedIdentity);

            using HttpClient restartedAdmin = new() { BaseAddress = new Uri(restartedIdentity.Endpoint) };
            HttpResponseMessage backupResponse = await restartedAdmin.PostAsJsonAsync("/harness/backup", new PathRequest(backupPath));
            backupResponse.EnsureSuccessStatusCode();
            backupResult = await backupResponse.Content.ReadFromJsonAsync<BackupEndpointResult>()
                ?? throw new InvalidDataException("Backup response was empty.");
            Directory.CreateDirectory(inconsistentPath);
            File.Copy(Path.Combine(backupPath, "manifest.json"), Path.Combine(inconsistentPath, "manifest.json"));
            BackupValidationEndpointResult inconsistent = (await (await restartedAdmin.PostAsJsonAsync(
                "/harness/validate-backup", new PathRequest(inconsistentPath))).Content
                .ReadFromJsonAsync<BackupValidationEndpointResult>())!;
            inconsistentRejected = !inconsistent.Valid;
            if (!inconsistentRejected) throw new InvalidDataException("An inconsistent backup set was accepted.");

            string targetConnectionString = await CreateRestoreDatabaseAsync(connectionString, restoreDatabase);
            HttpResponseMessage restoreResponse = await restartedAdmin.PostAsJsonAsync(
                "/harness/restore",
                new RestoreRequest(backupPath, restoredRoot, targetConnectionString));
            restoreResponse.EnsureSuccessStatusCode();
            await StopServerAsync(sourceServer, restartedIdentity.Endpoint);
            sourceServer.Dispose();
            sourceServer = null;

            restoredServer = StartServer(restoredRoot, schema, restoredReadyFile, bucket, targetConnectionString, accessKey, secretKey, tokenKey, pgDump, pgRestore);
            restoredIdentity = await WaitForServerAsync(restoredReadyFile, restoredServer);
            await WriteCheckpointAsync("verifying-restore", restoredIdentity);
            using AmazonS3Client restoredClient = CreateClient(restoredIdentity.Endpoint, accessKey, secretKey);
            await VerifyRetainedAsync(restoredClient, bucket, retained, counters);
            await VerifyDeniedAsync(restoredIdentity.Endpoint, bucket, counters);
            using HttpClient restoredAdmin = new() { BaseAddress = new Uri(restoredIdentity.Endpoint) };
            restoredReconciliation = await restoredAdmin.GetFromJsonAsync<ReconciliationResult>("/harness/reconcile")
                ?? throw new InvalidDataException("Restored reconciliation response was empty.");
            EnsureReconciled(restoredReconciliation, "restored");
            restoreVerified = true;
            await StopServerAsync(restoredServer, restoredIdentity.Endpoint);
            restoredServer.Dispose();
            restoredServer = null;

            sourceServer = StartServer(root, schema, readyFile, bucket, connectionString, accessKey, secretKey, tokenKey, pgDump, pgRestore);
            ServerIdentity finalSource = await WaitForServerAsync(readyFile, sourceServer);
            await WriteCheckpointAsync("verifying-original", finalSource);
            using AmazonS3Client finalSourceClient = CreateClient(finalSource.Endpoint, accessKey, secretKey);
            await VerifyRetainedAsync(finalSourceClient, bucket, retained, counters);
            originalVerifiedAfterRestore = true;
            await StopServerAsync(sourceServer, finalSource.Endpoint);
            sourceServer.Dispose();
            sourceServer = null;

            ValidateBudgets();
            await DropSchemaAsync(connectionString, schema);
            await DropDatabaseAsync(connectionString, restoreDatabase);
            DeleteRunResource(root);
            DeleteRunResource(restoredRoot);
            DeleteRunResource(backupPath);
            DeleteRunResource(inconsistentPath);
            cleanedOwnedResources = true;
        }
        catch (Exception exception)
        {
            terminalError = exception.GetType().Name + ": " + exception.Message;
            counters.AddFailure("terminal", exception);
        }
        finally
        {
            if (sourceServer is not null) await StopOwnedProcessAsync(sourceServer);
            if (restoredServer is not null) await StopOwnedProcessAsync(restoredServer);
            if (terminalError is not null)
            {
                try
                {
                    await DropSchemaAsync(connectionString, schema);
                    await DropDatabaseAsync(connectionString, restoreDatabase);
                    DeleteRunResource(root);
                    DeleteRunResource(restoredRoot);
                    DeleteRunResource(backupPath);
                    DeleteRunResource(inconsistentPath);
                    cleanedOwnedResources = true;
                }
                catch (Exception cleanupException)
                {
                    counters.AddFailure("cleanup", cleanupException);
                }
            }
            DateTimeOffset endedAt = DateTimeOffset.UtcNow;
            SoakRunSummary summary = new(
                options.RunId,
                "local validation",
                "0.1.0-rc.2",
                "4c5bbe953687023018eb3d2ca230f75e4e6f6c94",
                startedAt,
                endedAt,
                workload.Elapsed,
                options,
                budgets,
                environment,
                candidatePackages,
                assemblies,
                sourceIdentity,
                restartedIdentity,
                restoredIdentity,
                counters.Snapshot(),
                samples,
                sourceReconciliation,
                restoredReconciliation,
                backupResult,
                inconsistentRejected,
                restartVerified,
                restoreVerified,
                originalVerifiedAfterRestore,
                cleanedOwnedResources,
                terminalError,
                terminalError is null && counters.UnexpectedFailures == 0);
            await WriteJsonAtomicAsync(options.OutputPath, summary);
            await WriteCheckpointAsync(summary.Passed ? "completed" : "failed", sourceIdentity, summary);
            Console.WriteLine(options.OutputPath);
            if (!summary.Passed) Environment.ExitCode = 1;
        }

        async Task WorkAsync(int worker, string endpoint, CancellationToken cancellationToken)
        {
            using AmazonS3Client client = CreateClient(endpoint, accessKey, secretKey);
            int sequence = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                sequence++;
                string key = $"transient/worker-{worker}/slot-{sequence % 16}.bin";
                int length = sequence % 20 == 0 ? options.LargeObjectBytes : options.SmallObjectBytes;
                byte[] initial = CreateContent(length, worker * 100000 + sequence);
                byte[] overwrite = CreateContent(length, worker * 200000 + sequence);
                try
                {
                    await counters.MeasureAsync("upload", async () =>
                    {
                        await PutAsync(client, bucket, key, initial, cancellationToken);
                        counters.AddUploaded(initial.Length);
                    });
                    await counters.MeasureAsync("metadata", async () =>
                    {
                        GetObjectMetadataResponse head = await client.GetObjectMetadataAsync(bucket, key, cancellationToken);
                        if (head.ContentLength != initial.Length) throw new InvalidDataException("Metadata length mismatch.");
                        counters.AddIntegrityCheck();
                    });
                    await counters.MeasureAsync("read", () => VerifyObjectAsync(client, bucket, key, initial, counters, cancellationToken));
                    if (sequence % 4 == 0)
                        await counters.MeasureAsync("list-pagination", () => VerifyListingAsync(client, bucket, retained, cancellationToken));
                    await counters.MeasureAsync("range-explicit", () => VerifyRangeAsync(client, bucket, key, initial, new ByteRange(3, Math.Min(initial.Length - 1, 4098)), 3, Math.Min(initial.Length, 4099), counters, cancellationToken));
                    await counters.MeasureAsync("range-open", () => VerifyRangeAsync(client, bucket, key, initial, new ByteRange("bytes=17-"), 17, initial.Length, counters, cancellationToken));
                    int suffix = Math.Min(257, initial.Length);
                    await counters.MeasureAsync("range-suffix", () => VerifyRangeAsync(client, bucket, key, initial, new ByteRange($"bytes=-{suffix}"), initial.Length - suffix, initial.Length, counters, cancellationToken));
                    await counters.MeasureAsync("overwrite", async () =>
                    {
                        await PutAsync(client, bucket, key, overwrite, cancellationToken);
                        counters.AddUploaded(overwrite.Length);
                    });
                    await counters.MeasureAsync("overwrite-read", () => VerifyObjectAsync(client, bucket, key, overwrite, counters, cancellationToken));
                    if (sequence % 10 == 0)
                        await counters.MeasureAsync("cancelled-overwrite", () => VerifyCancelledOverwriteAsync(client, bucket, $"cancel/worker-{worker}.bin", options.LargeObjectBytes, counters, cancellationToken));
                    await counters.MeasureAsync("delete", async () => await client.DeleteObjectAsync(bucket, key, cancellationToken));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    counters.AddFailure($"worker-{worker}", exception);
                    try { await client.DeleteObjectAsync(bucket, key, CancellationToken.None); } catch { }
                }
            }
        }

        async Task SampleAsync(string endpoint, CancellationToken cancellationToken)
        {
            using HttpClient metrics = new() { BaseAddress = new Uri(endpoint) };
            DateTimeOffset previousAt = DateTimeOffset.UtcNow;
            SoakCounterSnapshot previous = counters.Snapshot();
            while (true)
            {
                SoakProcessMetrics process = await metrics.GetFromJsonAsync<SoakProcessMetrics>("/harness/metrics", cancellationToken)
                    ?? throw new InvalidDataException("Metrics response was empty.");
                SoakCounterSnapshot current = counters.Snapshot();
                double seconds = Math.Max((process.At - previousAt).TotalSeconds, 0.001);
                SoakSample sample = new(
                    process.At,
                    process,
                    current.CompletedOperations,
                    current.IntegrityChecks,
                    current.UnexpectedFailures,
                    current.Retries,
                    (current.CompletedOperations - previous.CompletedOperations) / seconds,
                    ((current.UploadedBytes - previous.UploadedBytes) + (current.DownloadedBytes - previous.DownloadedBytes)) / seconds / (1024 * 1024));
                lock (samples) samples.Add(sample);
                previousAt = process.At;
                previous = current;
                await WriteCheckpointAsync("running", sourceIdentity);
                await Task.Delay(options.CheckpointInterval, cancellationToken);
            }
        }

        async Task CleanupAsync(string endpoint, CancellationToken cancellationToken)
        {
            using HttpClient client = new() { BaseAddress = new Uri(endpoint) };
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                await counters.MeasureAsync("cleanup", async () =>
                {
                    using HttpResponseMessage response = await client.PostAsync("/harness/cleanup", null, cancellationToken);
                    response.EnsureSuccessStatusCode();
                });
            }
        }

        async Task WriteCheckpointAsync(string state, ServerIdentity? host, SoakRunSummary? final = null)
        {
            SoakCheckpoint checkpoint = new(
                options.RunId,
                state,
                startedAt,
                DateTimeOffset.UtcNow,
                options.Duration,
                workload.Elapsed,
                host?.ProcessId,
                host?.ProcessStartedAt,
                counters.Snapshot(),
                samples.LastOrDefault(),
                final?.Passed,
                terminalError);
            await WriteJsonAtomicAsync(options.CheckpointPath, checkpoint);
        }

        void ValidateBudgets()
        {
            SoakCounterSnapshot snapshot = counters.Snapshot();
            if (snapshot.UnexpectedFailures > budgets.UnexpectedFailures) throw new InvalidOperationException("Unexpected-failure budget exceeded.");
            if (snapshot.Retries > budgets.Retries) throw new InvalidOperationException("Retry budget exceeded.");
            foreach (OperationSnapshot operation in snapshot.Operations)
            {
                double limit = operation.Operation switch
                {
                    "cleanup" => budgets.CleanupP95Milliseconds,
                    "cancelled-overwrite" => budgets.ControlledCancellationP95Milliseconds,
                    _ => budgets.DataOperationP95Milliseconds
                };
                if (operation.P95Milliseconds > limit)
                    throw new InvalidOperationException($"Latency budget exceeded for {operation.Operation}: {operation.P95Milliseconds:N0} ms > {limit:N0} ms.");
            }
            if (samples.Any(item => item.Process.ManagedHeapBytes > budgets.ManagedHeapBytes)) throw new InvalidOperationException("Managed-heap budget exceeded.");
            if (samples.Any(item => item.Process.WorkingSetBytes > budgets.WorkingSetBytes)) throw new InvalidOperationException("Working-set budget exceeded.");
            if (samples.Any(item => item.Process.RootBytes > budgets.RunDiskBytes)) throw new InvalidOperationException("Disk budget exceeded.");
            if (samples.Any(item => item.Process.DatabaseConnections > budgets.PostgreSqlConnections)) throw new InvalidOperationException("PostgreSQL connection budget exceeded.");
        }
    }

    private static async Task<IReadOnlyList<RetainedObject>> SeedRetainedAsync(AmazonS3Client client, string bucket, SoakOptions options)
    {
        List<RetainedObject> retained = [];
        for (int index = 0; index < 12; index++)
        {
            byte[] content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                id = index,
                name = $"structured-{index}",
                values = Enumerable.Range(0, 64).Select(value => value * (index + 1)).ToArray()
            }));
            string key = $"retained/structured/item-{index:D2}.json";
            await PutAsync(client, bucket, key, content, CancellationToken.None, "application/json");
            retained.Add(new(key, content, Convert.ToHexStringLower(SHA256.HashData(content))));
        }
        for (int index = 0; index < 4; index++)
        {
            byte[] content = CreateContent(options.LargeObjectBytes, 900000 + index);
            string key = $"retained/large/object-{index:D2}.bin";
            await PutAsync(client, bucket, key, content, CancellationToken.None);
            retained.Add(new(key, content, Convert.ToHexStringLower(SHA256.HashData(content))));
        }
        return retained;
    }

    private static async Task VerifyRetainedAsync(AmazonS3Client client, string bucket, IReadOnlyList<RetainedObject> retained, SoakCounters counters)
    {
        await VerifyListingAsync(client, bucket, retained, CancellationToken.None);
        foreach (RetainedObject item in retained)
        {
            GetObjectMetadataResponse metadata = await client.GetObjectMetadataAsync(bucket, item.Key);
            if (metadata.ContentLength != item.Content.Length) throw new InvalidDataException("Retained metadata length mismatch.");
            await VerifyObjectAsync(client, bucket, item.Key, item.Content, counters, CancellationToken.None);
            int suffix = Math.Min(257, item.Content.Length);
            await VerifyRangeAsync(client, bucket, item.Key, item.Content, new ByteRange($"bytes=-{suffix}"), item.Content.Length - suffix, item.Content.Length, counters, CancellationToken.None);
        }
    }

    private static async Task VerifyListingAsync(AmazonS3Client client, string bucket, IReadOnlyList<RetainedObject> retained, CancellationToken cancellationToken)
    {
        HashSet<string> keys = [];
        string? token = null;
        do
        {
            ListObjectsV2Response page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = "retained/",
                MaxKeys = 3,
                ContinuationToken = token
            }, cancellationToken);
            foreach (S3Object item in page.S3Objects ?? []) keys.Add(item.Key);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
        if (retained.Any(item => !keys.Contains(item.Key))) throw new InvalidDataException("Paginated listing omitted a retained object.");
    }

    private static async Task DeletePrefixAsync(AmazonS3Client client, string bucket, string prefix)
    {
        string? token = null;
        do
        {
            ListObjectsV2Response page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                MaxKeys = 1000,
                ContinuationToken = token
            });
            foreach (S3Object item in page.S3Objects ?? []) await client.DeleteObjectAsync(bucket, item.Key);
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (token is not null);
    }

    private static async Task VerifyObjectAsync(
        AmazonS3Client client,
        string bucket,
        string key,
        byte[] expected,
        SoakCounters counters,
        CancellationToken cancellationToken)
    {
        using GetObjectResponse response = await client.GetObjectAsync(bucket, key, cancellationToken);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        int read;
        long length = 0;
        while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        string wanted = Convert.ToHexStringLower(SHA256.HashData(expected));
        if (length != expected.Length || !string.Equals(actual, wanted, StringComparison.Ordinal))
            throw new InvalidDataException("Complete-object integrity mismatch.");
        counters.AddDownloaded(length);
        counters.AddIntegrityCheck();
    }

    private static async Task VerifyRangeAsync(
        AmazonS3Client client,
        string bucket,
        string key,
        byte[] expected,
        ByteRange range,
        int start,
        int endExclusive,
        SoakCounters counters,
        CancellationToken cancellationToken)
    {
        using GetObjectResponse response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = range
        }, cancellationToken);
        using MemoryStream content = new();
        await response.ResponseStream.CopyToAsync(content, cancellationToken);
        if (!content.ToArray().AsSpan().SequenceEqual(expected.AsSpan(start, endExclusive - start)))
            throw new InvalidDataException("Range integrity mismatch.");
        counters.AddDownloaded(content.Length);
        counters.AddIntegrityCheck();
    }

    private static async Task VerifyCancelledOverwriteAsync(
        AmazonS3Client client,
        string bucket,
        string key,
        int largeBytes,
        SoakCounters counters,
        CancellationToken outerCancellation)
    {
        byte[] baseline = Encoding.ASCII.GetBytes("preserve-after-cancel");
        await PutAsync(client, bucket, key, baseline, outerCancellation);
        byte[] replacement = CreateContent(largeBytes, 777777);
        await using MemoryStream memory = new(replacement, writable: false);
        await using SlowReadStream slow = new(memory, TimeSpan.FromMilliseconds(5));
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(outerCancellation);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(20));
        try
        {
            await client.PutObjectAsync(new AwsPutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = slow,
                UseChunkEncoding = false
            }, timeout.Token);
            throw new InvalidOperationException("Controlled overwrite completed before cancellation.");
        }
        catch (OperationCanceledException) when (!outerCancellation.IsCancellationRequested)
        {
            counters.AddExpectedCancellation();
        }
        await VerifyObjectAsync(client, bucket, key, baseline, counters, outerCancellation);
        await client.DeleteObjectAsync(bucket, key, outerCancellation);
    }

    private static async Task VerifyDeniedAsync(string endpoint, string bucket, SoakCounters counters)
    {
        using AmazonS3Client denied = CreateClient(endpoint, "DENIED", "denied-secret");
        try
        {
            await denied.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            throw new InvalidOperationException("Invalid credentials were accepted.");
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            counters.AddExpectedAuthorizationDenial();
        }
    }

    private static async Task PutAsync(
        AmazonS3Client client,
        string bucket,
        string key,
        byte[] content,
        CancellationToken cancellationToken,
        string contentType = "application/octet-stream")
    {
        await client.PutObjectAsync(new AwsPutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = new MemoryStream(content, writable: false),
            ContentType = contentType,
            UseChunkEncoding = false
        }, cancellationToken);
    }

    private static AmazonS3Client CreateClient(string endpoint, string accessKey, string secretKey) =>
        new(new BasicAWSCredentials(accessKey, secretKey), new AmazonS3Config
        {
            ServiceURL = endpoint + "/s3",
            AuthenticationRegion = Region,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            MaxErrorRetry = 0
        });

    private static Process StartServer(
        string root,
        string schema,
        string readyFile,
        string bucket,
        string connectionString,
        string accessKey,
        string secretKey,
        string tokenKey,
        string pgDump,
        string pgRestore)
    {
        if (File.Exists(readyFile)) File.Delete(readyFile);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve the harness executable.");
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("soak-server");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(schema);
        start.ArgumentList.Add(readyFile);
        start.ArgumentList.Add(bucket);
        start.Environment["AUDORIX_OBJECTSTORAGE_POSTGRES"] = connectionString;
        start.Environment["AUDORIX_SOAK_ACCESS_KEY"] = accessKey;
        start.Environment["AUDORIX_SOAK_SECRET_KEY"] = secretKey;
        start.Environment["AUDORIX_SOAK_TOKEN_KEY"] = tokenKey;
        start.Environment["AUDORIX_PG_DUMP_PATH"] = pgDump;
        start.Environment["AUDORIX_PG_RESTORE_PATH"] = pgRestore;
        Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the soak host.");
        process.OutputDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Console.Error.WriteLine("host: " + eventArgs.Data); };
        process.ErrorDataReceived += (_, eventArgs) => { if (eventArgs.Data is not null) Console.Error.WriteLine("host: " + eventArgs.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<ServerIdentity> WaitForServerAsync(string readyFile, Process process)
    {
        for (int attempt = 0; attempt < 600; attempt++)
        {
            if (File.Exists(readyFile))
            {
                try
                {
                    ServerIdentity? identity = JsonSerializer.Deserialize<ServerIdentity>(await File.ReadAllTextAsync(readyFile), JsonOptions);
                    if (identity is not null) return identity;
                }
                catch (IOException)
                {
                    // The atomic publisher may still be releasing the file on Windows.
                }
                catch (JsonException)
                {
                    // Retry until the complete readiness record is visible.
                }
            }
            if (process.HasExited) throw new InvalidOperationException("Soak host exited before readiness.");
            await Task.Delay(100);
        }
        throw new TimeoutException("Soak host did not become ready.");
    }

    private static async Task StopServerAsync(Process process, string endpoint)
    {
        using HttpClient client = new() { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(10) };
        try { await client.PostAsync("/harness/stop", null); } catch (HttpRequestException) { }
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static async Task<EnvironmentEvidence> CaptureEnvironmentAsync(
        NpgsqlConnectionStringBuilder connection,
        DriveInfo drive,
        string pgRestore)
    {
        string serverVersion;
        await using (NpgsqlConnection database = new(connection.ConnectionString))
        {
            await database.OpenAsync();
            serverVersion = database.PostgreSqlVersion.ToString();
        }
        return new(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.Version.ToString(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            drive.AvailableFreeSpace,
            serverVersion,
            await ReadToolVersionAsync(pgRestore),
            $"Kestrel loopback HTTP; PostgreSQL host {connection.Host}:{connection.Port}; single local machine");
    }

    private static async Task<IReadOnlyList<AssemblyEvidence>> CaptureAssemblyEvidenceAsync()
    {
        Assembly[] assemblies =
        [
            typeof(IObjectStorageEngine).Assembly,
            typeof(PostgreSqlObjectStorageBackupService).Assembly,
            typeof(S3EndpointOptions).Assembly
        ];
        List<AssemblyEvidence> evidence = [];
        foreach (Assembly assembly in assemblies)
        {
            string path = assembly.Location;
            evidence.Add(new(
                assembly.GetName().Name!,
                assembly.GetName().Version?.ToString() ?? "unknown",
                path,
                await HashFileAsync(path)));
        }
        return evidence;
    }

    private static async Task<IReadOnlyList<CandidatePackageEvidence>> CaptureCandidatePackagesAsync()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "artifacts", "candidates", "4c5bbe9", "packages")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Pinned candidate package directory was not found.");
        string packageDirectory = Path.Combine(directory.FullName, "artifacts", "candidates", "4c5bbe9", "packages");
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["Audorix.ObjectStorage.0.1.0-rc.2.nupkg"] = "F7A1847C1B9968BD1FF37CE27639D6ABD0256CAFFAF363372A128A3FDA4D08FE",
            ["Audorix.ObjectStorage.Api.0.1.0-rc.2.nupkg"] = "C283BDCD7CCDD75702B0D342781D366DB796AECC9A4F49BCBC6BD9D530510432",
            ["Audorix.ObjectStorage.PostgreSql.0.1.0-rc.2.nupkg"] = "0858DC2761023F421397B598C08C422B4C77CADB313462B471E0D9E173BA2CB4",
            ["Audorix.ObjectStorage.S3.0.1.0-rc.2.nupkg"] = "A88788A91C99EA99E9D1CAE882472D150B92A6C161642628B57CCD1ACF60D00E"
        };
        List<CandidatePackageEvidence> result = [];
        foreach ((string file, string expectedHash) in expected)
        {
            string path = Path.Combine(packageDirectory, file);
            string actualHash = await HashFileAsync(path);
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("Pinned candidate package hash mismatch: " + file);
            result.Add(new(file, new FileInfo(path).Length, actualHash));
        }
        return result;
    }

    private static async Task<string> ReadToolVersionAsync(string executable)
    {
        ProcessStartInfo start = new(executable, "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }

    private static string FindPostgreSqlTool(string name)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        string? path = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).OrderDescending().FirstOrDefault()
            : null;
        return path ?? throw new FileNotFoundException($"Required PostgreSQL tool was not found: {name}");
    }

    private static async Task<string> CreateRestoreDatabaseAsync(string sourceConnectionString, string databaseName)
    {
        NpgsqlConnectionStringBuilder admin = new(sourceConnectionString) { Database = "postgres", Pooling = false };
        await using NpgsqlConnection connection = new(admin.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"create database \"{databaseName}\";", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(sourceConnectionString) { Database = databaseName, Pooling = false }.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string sourceConnectionString, string databaseName)
    {
        if (!databaseName.StartsWith("audorix_restore_", StringComparison.Ordinal)) return;
        NpgsqlConnectionStringBuilder admin = new(sourceConnectionString) { Database = "postgres", Pooling = false };
        await using NpgsqlConnection connection = new(admin.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"drop database if exists \"{databaseName}\" with (force);", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        if (!schema.StartsWith("audorix_soak_", StringComparison.Ordinal)) return;
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new($"drop schema if exists \"{schema}\" cascade;", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string LoadConnectionString()
    {
        string? configured = Environment.GetEnvironmentVariable("AUDORIX_OBJECTSTORAGE_POSTGRES");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
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
        throw new InvalidOperationException("AUDORIX_OBJECTSTORAGE_POSTGRES or the ignored local database.json is required.");
    }

    private static byte[] CreateContent(int length, int seed)
    {
        byte[] content = new byte[length];
        for (int index = 0; index < content.Length; index++) content[index] = (byte)((index * 31L + seed * 17L) % 251);
        return content;
    }

    private static long GetDirectorySize(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        try
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(path).Length; }
                catch (FileNotFoundException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        return total;
    }

    private static void EnsureReconciled(ReconciliationResult result, string label)
    {
        if (result.MissingBlobs != 0 || result.UnreferencedBlobs != 0 || result.StagingFiles != 0)
            throw new InvalidDataException($"{label} reconciliation found missing, unreferenced, or staging files.");
    }

    private static async Task<string> HashFileAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException(name + " is required.");

    private static void DeleteRunResource(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed record RetainedObject(string Key, byte[] Content, string Sha256);
    private sealed record PathRequest(string Path);
    private sealed record RestoreRequest(string BackupPath, string StorageRoot, string TargetConnectionString);
    internal sealed record ServerIdentity(string Endpoint, int ProcessId, string ProcessStartedAt, string ExecutablePath, string ExecutableSha256);
    internal sealed record SoakProcessMetrics(DateTimeOffset At, long ManagedHeapBytes, long WorkingSetBytes, double TotalProcessorMilliseconds, long RootBytes, int StagingFiles, int DatabaseConnections, int MaxDatabaseConnections, long DatabaseBytes);
    internal sealed record ReconciliationResult(int ReferencedBlobs, int PhysicalBlobs, int MissingBlobs, int UnreferencedBlobs, int StagingFiles);
    internal sealed record BackupEndpointResult(long ManifestObjectCount, long ValidatedObjectCount, string BackupSetId);
    private sealed record BackupValidationEndpointResult(bool Valid, long ValidatedObjectCount, string? ErrorType);
    internal sealed record AssemblyEvidence(string Name, string Version, string Path, string Sha256);
    internal sealed record CandidatePackageEvidence(string File, long Bytes, string Sha256);
    internal sealed record EnvironmentEvidence(string OperatingSystem, string Architecture, string Runtime, int LogicalProcessors, long AvailableManagedMemoryBytes, long InitialFreeDiskBytes, string PostgreSqlServerVersion, string PostgreSqlClientVersion, string NetworkTopology);
    internal sealed record AcceptanceBudgets(int UnexpectedFailures, int Retries, double DataOperationP95Milliseconds, double CleanupP95Milliseconds, double ControlledCancellationP95Milliseconds, long ManagedHeapBytes, long WorkingSetBytes, long RunDiskBytes, int PostgreSqlConnections, int ResidualStagingFiles, int MissingOrUnreferencedBlobs);

    internal sealed record SoakOptions(
        string RunId,
        TimeSpan Duration,
        int Concurrency,
        int SmallObjectBytes,
        int LargeObjectBytes,
        long DiskBudgetBytes,
        TimeSpan CheckpointInterval,
        string RunDirectory,
        string OutputPath,
        string CheckpointPath)
    {
        public static SoakOptions Parse(string[] args)
        {
            Dictionary<string, string> values = args
                .Where(value => !string.Equals(value, "--soak", StringComparison.OrdinalIgnoreCase))
                .Chunk(2)
                .Where(chunk => chunk.Length == 2)
                .ToDictionary(chunk => chunk[0], chunk => chunk[1], StringComparer.OrdinalIgnoreCase);
            string runId = values.GetValueOrDefault("--run-id", DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
            int duration = int.Parse(values.GetValueOrDefault("--duration-seconds", "7200"));
            int concurrency = int.Parse(values.GetValueOrDefault("--concurrency", "4"));
            int smallKiB = int.Parse(values.GetValueOrDefault("--small-kib", "64"));
            int largeMiB = int.Parse(values.GetValueOrDefault("--large-mib", "4"));
            int diskMiB = int.Parse(values.GetValueOrDefault("--disk-budget-mib", "1024"));
            int checkpointSeconds = int.Parse(values.GetValueOrDefault("--checkpoint-seconds", "60"));
            string runDirectory = Path.GetFullPath(values.GetValueOrDefault("--run-dir", Path.Combine(Directory.GetCurrentDirectory(), "soak-" + runId)));
            string output = Path.GetFullPath(values.GetValueOrDefault("--output", Path.Combine(runDirectory, "summary.json")));
            string checkpoint = Path.GetFullPath(values.GetValueOrDefault("--checkpoint", Path.Combine(runDirectory, "checkpoint.json")));
            if (duration < 1 || concurrency < 1 || concurrency > 64 || smallKiB < 1 || largeMiB < 1 || diskMiB < 128 || checkpointSeconds < 5)
                throw new ArgumentException("Invalid soak limits.");
            return new(runId, TimeSpan.FromSeconds(duration), concurrency, checked(smallKiB * 1024), checked(largeMiB * 1024 * 1024), checked(diskMiB * 1024L * 1024), TimeSpan.FromSeconds(checkpointSeconds), runDirectory, output, checkpoint);
        }
    }

    internal sealed record OperationSnapshot(string Operation, long Count, double MinimumMilliseconds, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaximumMilliseconds, double AverageMilliseconds);
    internal sealed record SoakCounterSnapshot(long CompletedOperations, long IntegrityChecks, long ExpectedCancellations, long ExpectedAuthorizationDenials, long UnexpectedFailures, long Retries, long UploadedBytes, long DownloadedBytes, IReadOnlyList<string> Failures, IReadOnlyList<OperationSnapshot> Operations);
    internal sealed record SoakSample(DateTimeOffset At, SoakProcessMetrics Process, long CompletedOperations, long IntegrityChecks, long UnexpectedFailures, long Retries, double OperationsPerSecond, double TransferMiBPerSecond);
    internal sealed record SoakCheckpoint(string RunId, string State, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, TimeSpan RequestedDuration, TimeSpan WorkloadDuration, int? HostProcessId, string? HostProcessStartedAt, SoakCounterSnapshot Counters, SoakSample? LatestMetrics, bool? Passed, string? TerminalError);
    internal sealed record SoakRunSummary(string RunId, string EnvironmentLabel, string PackageVersion, string PackageSourceCommit, DateTimeOffset StartedAt, DateTimeOffset EndedAt, TimeSpan WorkloadDuration, SoakOptions Options, AcceptanceBudgets AcceptanceBudgets, EnvironmentEvidence Environment, IReadOnlyList<CandidatePackageEvidence> CandidatePackages, IReadOnlyList<AssemblyEvidence> LoadedAssemblies, ServerIdentity? InitialHost, ServerIdentity? RestartedHost, ServerIdentity? RestoredHost, SoakCounterSnapshot Counters, IReadOnlyList<SoakSample> Metrics, ReconciliationResult? SourceReconciliation, ReconciliationResult? RestoredReconciliation, BackupEndpointResult? Backup, bool InconsistentBackupRejected, bool RestartVerified, bool RestoreVerified, bool OriginalVerifiedAfterRestore, bool CleanedOwnedResources, string? TerminalError, bool Passed);

    private sealed class SoakCounters
    {
        private readonly ConcurrentDictionary<string, LatencyHistogram> latencies = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> failures = new();
        private long completedOperations;
        private long integrityChecks;
        private long expectedCancellations;
        private long expectedAuthorizationDenials;
        private long unexpectedFailures;
        private long uploadedBytes;
        private long downloadedBytes;

        public long UnexpectedFailures => Interlocked.Read(ref unexpectedFailures);
        public async Task MeasureAsync(string operation, Func<Task> action)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            await action();
            elapsed.Stop();
            latencies.GetOrAdd(operation, _ => new()).Add(elapsed.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref completedOperations);
        }
        public void AddIntegrityCheck() => Interlocked.Increment(ref integrityChecks);
        public void AddExpectedCancellation() => Interlocked.Increment(ref expectedCancellations);
        public void AddExpectedAuthorizationDenial() => Interlocked.Increment(ref expectedAuthorizationDenials);
        public void AddUploaded(long bytes) => Interlocked.Add(ref uploadedBytes, bytes);
        public void AddDownloaded(long bytes) => Interlocked.Add(ref downloadedBytes, bytes);
        public void AddFailure(string context, Exception exception)
        {
            Interlocked.Increment(ref unexpectedFailures);
            if (failures.Count < 100) failures.Enqueue($"{context}: {exception.GetType().Name}: {exception.Message}");
        }
        public SoakCounterSnapshot Snapshot() => new(
            Interlocked.Read(ref completedOperations),
            Interlocked.Read(ref integrityChecks),
            Interlocked.Read(ref expectedCancellations),
            Interlocked.Read(ref expectedAuthorizationDenials),
            Interlocked.Read(ref unexpectedFailures),
            0,
            Interlocked.Read(ref uploadedBytes),
            Interlocked.Read(ref downloadedBytes),
            failures.ToArray(),
            latencies.OrderBy(pair => pair.Key).Select(pair => pair.Value.Snapshot(pair.Key)).ToArray());
    }

    private sealed class LatencyHistogram
    {
        private const int MaximumBucket = 60000;
        private readonly long[] buckets = new long[MaximumBucket + 1];
        private long count;
        private double minimum = double.MaxValue;
        private double maximum;
        private double total;
        private readonly object gate = new();

        public void Add(double milliseconds)
        {
            int bucket = Math.Clamp((int)Math.Ceiling(milliseconds), 0, MaximumBucket);
            lock (gate)
            {
                buckets[bucket]++;
                count++;
                minimum = Math.Min(minimum, milliseconds);
                maximum = Math.Max(maximum, milliseconds);
                total += milliseconds;
            }
        }
        public OperationSnapshot Snapshot(string operation)
        {
            lock (gate)
            {
                return new(operation, count, count == 0 ? 0 : minimum, Percentile(0.50), Percentile(0.95), Percentile(0.99), maximum, count == 0 ? 0 : total / count);
            }
        }
        private double Percentile(double percentile)
        {
            if (count == 0) return 0;
            long target = (long)Math.Ceiling(count * percentile);
            long seen = 0;
            for (int index = 0; index < buckets.Length; index++)
            {
                seen += buckets[index];
                if (seen >= target) return index;
            }
            return MaximumBucket;
        }
    }

    private sealed class SlowReadStream(Stream inner, TimeSpan delay) : Stream
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
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); await base.DisposeAsync(); }
    }
}
