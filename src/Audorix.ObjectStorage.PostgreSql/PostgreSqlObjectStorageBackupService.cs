using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Audorix.ObjectStorage.Metadata;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Audorix.ObjectStorage.PostgreSql;

public sealed class PostgreSqlObjectStorageBackupService
{
    public const int CurrentFormatVersion = 1;
    public const int CurrentMetadataSchemaVersion = 1;
    private const string ManifestFileName = "manifest.json";
    private const string InventoryFileName = "inventory.ndjson";
    private const string DatabaseDumpFileName = "metadata.dump";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions InventoryJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IObjectStorageEngine _storage;
    private readonly IObjectStorageMetadataStore _metadata;
    private readonly ObjectStorageOptions _storageOptions;
    private readonly PostgreSqlObjectStorageOptions _databaseOptions;
    private readonly PostgreSqlBackupOptions _backupOptions;

    public PostgreSqlObjectStorageBackupService(
        IObjectStorageEngine storage,
        IObjectStorageMetadataStore metadata,
        IOptions<ObjectStorageOptions> storageOptions,
        IOptions<PostgreSqlObjectStorageOptions> databaseOptions,
        IOptions<PostgreSqlBackupOptions> backupOptions)
    {
        _storage = storage;
        _metadata = metadata;
        _storageOptions = storageOptions.Value;
        _databaseOptions = databaseOptions.Value;
        _backupOptions = backupOptions.Value;
    }

    public async Task<ObjectStorageBackupManifest> CreateAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        string destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("The backup destination already exists.");
        }

        string parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException("A backup parent directory is required.");
        Directory.CreateDirectory(parent);
        string backupSetId = Guid.NewGuid().ToString("N");
        string temporary = Path.Combine(parent, ".audorix-backup-" + backupSetId + ".incomplete");
        Directory.CreateDirectory(temporary);
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        try
        {
            await using IAsyncDisposable maintenance = await _storage.EnterMaintenanceAsync(cancellationToken).ConfigureAwait(false);
            string dumpPath = Path.Combine(temporary, DatabaseDumpFileName);
            await RunPostgreSqlToolAsync(
                _backupOptions.PgDumpPath,
                _databaseOptions.ConnectionString,
                ["--format=custom", "--no-owner", "--no-privileges", "--schema=" + _databaseOptions.SchemaName, "--file=" + dumpPath],
                cancellationToken).ConfigureAwait(false);

            string backupBlobs = Path.Combine(temporary, "blobs");
            Directory.CreateDirectory(backupBlobs);
            string inventoryPath = Path.Combine(temporary, InventoryFileName);
            long objectCount = 0;
            await using (FileStream inventoryStream = new(inventoryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, _backupOptions.CopyBufferSize, FileOptions.Asynchronous))
            await using (StreamWriter writer = new(inventoryStream, new UTF8Encoding(false), _backupOptions.CopyBufferSize, leaveOpen: false))
            {
                await foreach (string blobId in _metadata.EnumerateReferencedBlobIdsAsync(cancellationToken).ConfigureAwait(false))
                {
                    EnsureSafeBlobId(blobId);
                    string relativePath = "blobs/" + blobId + ".blob";
                    string source = Path.Combine(_storageOptions.StorageRootPath, "blobs", blobId + ".blob");
                    string target = Path.Combine(backupBlobs, blobId + ".blob");
                    (long length, string hash) = await CopyAndHashAsync(source, target, _backupOptions.CopyBufferSize, cancellationToken).ConfigureAwait(false);
                    BackupInventoryEntry entry = new(backupSetId, blobId, relativePath, length, hash);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry, InventoryJsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
                    objectCount++;
                }
            }

            BackupFileDescriptor dump = (await DescribeFileAsync(dumpPath, cancellationToken).ConfigureAwait(false)) with { Path = DatabaseDumpFileName };
            BackupFileDescriptor inventory = (await DescribeFileAsync(inventoryPath, cancellationToken).ConfigureAwait(false)) with { Path = InventoryFileName };
            ObjectStorageBackupManifest manifest = new(
                CurrentFormatVersion,
                CurrentMetadataSchemaVersion,
                backupSetId,
                _databaseOptions.SchemaName,
                createdAt,
                DateTimeOffset.UtcNow,
                dump,
                inventory,
                objectCount,
                Complete: true);
            await File.WriteAllTextAsync(
                Path.Combine(temporary, ManifestFileName),
                JsonSerializer.Serialize(manifest, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);

            await ValidateAsync(temporary, cancellationToken).ConfigureAwait(false);
            Directory.Move(temporary, destination);
            return manifest;
        }
        catch
        {
            TryDeleteDirectory(temporary);
            throw;
        }
    }

    public async Task<BackupValidationResult> ValidateAsync(string backupDirectory, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(backupDirectory);
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new BackupValidationException("The backup manifest is missing; the set is incomplete.");
        }

        ObjectStorageBackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ObjectStorageBackupManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new BackupValidationException("The backup manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new BackupValidationException("The backup manifest is invalid JSON: " + exception.Message);
        }

        if (manifest.FormatVersion != CurrentFormatVersion || manifest.MetadataSchemaVersion != CurrentMetadataSchemaVersion)
        {
            throw new BackupValidationException("The backup manifest or metadata schema version is unsupported.");
        }

        if (!manifest.Complete || !IsIdentifier(manifest.BackupSetId) || !IsSchemaName(manifest.MetadataSchema))
        {
            throw new BackupValidationException("The backup manifest is incomplete or invalid.");
        }

        if (!string.Equals(manifest.DatabaseDump.Path, DatabaseDumpFileName, StringComparison.Ordinal)
            || !string.Equals(manifest.Inventory.Path, InventoryFileName, StringComparison.Ordinal))
        {
            throw new BackupValidationException("The manifest contains an unexpected backup component path.");
        }

        string dumpPath = ResolveSafePath(root, manifest.DatabaseDump.Path);
        string inventoryPath = ResolveSafePath(root, manifest.Inventory.Path);
        await ValidateDescriptorAsync(dumpPath, manifest.DatabaseDump, cancellationToken).ConfigureAwait(false);
        await ValidateDumpArchiveAsync(dumpPath, cancellationToken).ConfigureAwait(false);
        await ValidateDescriptorAsync(inventoryPath, manifest.Inventory, cancellationToken).ConfigureAwait(false);

        long count = 0;
        string? previousBlobId = null;
        await foreach (BackupInventoryEntry entry in ReadInventoryAsync(inventoryPath, cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(entry.BackupSetId, manifest.BackupSetId, StringComparison.Ordinal))
            {
                throw new BackupValidationException("The inventory belongs to a different backup set.");
            }

            EnsureSafeBlobId(entry.BlobId);
            string expectedPath = "blobs/" + entry.BlobId + ".blob";
            if (!string.Equals(entry.Path, expectedPath, StringComparison.Ordinal))
            {
                throw new BackupValidationException("The inventory contains an unsafe or mismatched blob path.");
            }

            if (previousBlobId is not null && string.CompareOrdinal(previousBlobId, entry.BlobId) >= 0)
            {
                throw new BackupValidationException("The inventory contains duplicate or unsorted blob entries.");
            }

            previousBlobId = entry.BlobId;
            string blobPath = ResolveSafePath(root, entry.Path);
            await ValidateFileAsync(blobPath, entry.Length, entry.Sha256, cancellationToken).ConfigureAwait(false);
            count++;
        }

        if (count != manifest.ObjectCount)
        {
            throw new BackupValidationException("The inventory object count does not match the manifest.");
        }

        return new BackupValidationResult(manifest, count);
    }

    public async Task RestoreAsync(
        string backupDirectory,
        string restoredStorageRoot,
        string targetConnectionString,
        CancellationToken cancellationToken = default)
    {
        BackupValidationResult validation = await ValidateAsync(backupDirectory, cancellationToken).ConfigureAwait(false);
        string schema = validation.Manifest.MetadataSchema;
        if (Directory.Exists(restoredStorageRoot) || File.Exists(restoredStorageRoot))
        {
            throw new IOException("The restore storage root must not already exist.");
        }

        await EnsureSchemaAbsentAsync(targetConnectionString, schema, cancellationToken).ConfigureAwait(false);
        string root = Path.GetFullPath(backupDirectory);
        string temporaryRoot = Path.GetFullPath(restoredStorageRoot) + ".incomplete-" + validation.Manifest.BackupSetId;
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "blobs"));
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "staging"));
        bool schemaCreated = false;
        try
        {
            await RunPostgreSqlToolAsync(
                _backupOptions.PgRestorePath,
                targetConnectionString,
                ["--exit-on-error", "--no-owner", "--no-privileges", Path.Combine(root, validation.Manifest.DatabaseDump.Path)],
                cancellationToken).ConfigureAwait(false);
            schemaCreated = true;

            string inventoryPath = Path.Combine(root, validation.Manifest.Inventory.Path);
            await foreach (BackupInventoryEntry entry in ReadInventoryAsync(inventoryPath, cancellationToken).ConfigureAwait(false))
            {
                await CopyAndHashAsync(
                    Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)),
                    Path.Combine(temporaryRoot, "blobs", entry.BlobId + ".blob"),
                    _backupOptions.CopyBufferSize,
                    cancellationToken).ConfigureAwait(false);
            }

            await ReconcileRestoredReferencesAsync(targetConnectionString, schema, inventoryPath, cancellationToken).ConfigureAwait(false);
            Directory.Move(temporaryRoot, Path.GetFullPath(restoredStorageRoot));
        }
        catch
        {
            TryDeleteDirectory(temporaryRoot);
            if (schemaCreated || await SchemaExistsAsync(targetConnectionString, schema, CancellationToken.None).ConfigureAwait(false))
            {
                await DropSchemaAsync(targetConnectionString, schema, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task ReconcileRestoredReferencesAsync(
        string connectionString,
        string schema,
        string inventoryPath,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        string table = QuoteIdentifier(schema) + ".\"objects\"";
        await using NpgsqlCommand command = new($"select distinct blob_id collate \"C\" as blob_id from {table} order by blob_id collate \"C\";", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using IAsyncEnumerator<BackupInventoryEntry> inventory = ReadInventoryAsync(inventoryPath, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool hasInventory = await inventory.MoveNextAsync().ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!hasInventory || !string.Equals(reader.GetString(0), inventory.Current.BlobId, StringComparison.Ordinal))
            {
                throw new BackupValidationException("Restored database references do not match the blob inventory.");
            }

            hasInventory = await inventory.MoveNextAsync().ConfigureAwait(false);
        }

        if (hasInventory)
        {
            throw new BackupValidationException("The blob inventory contains entries not referenced by the restored database.");
        }
    }

    private static async IAsyncEnumerable<BackupInventoryEntry> ReadInventoryAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                throw new BackupValidationException("The inventory contains an empty entry.");
            }

            BackupInventoryEntry entry;
            try
            {
                entry = JsonSerializer.Deserialize<BackupInventoryEntry>(line, InventoryJsonOptions)
                    ?? throw new BackupValidationException("The inventory contains an empty entry.");
            }
            catch (JsonException exception)
            {
                throw new BackupValidationException("The inventory contains invalid JSON: " + exception.Message);
            }

            yield return entry;
        }
    }

    private static async Task ValidateDescriptorAsync(string path, BackupFileDescriptor descriptor, CancellationToken cancellationToken) =>
        await ValidateFileAsync(path, descriptor.Length, descriptor.Sha256, cancellationToken).ConfigureAwait(false);

    private static async Task ValidateFileAsync(string path, long expectedLength, string expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new BackupValidationException("A required backup file is missing: " + Path.GetFileName(path));
        }

        FileInfo info = new(path);
        if (info.Length != expectedLength)
        {
            throw new BackupValidationException("A backup file length does not match its manifest: " + Path.GetFileName(path));
        }

        string hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupValidationException("A backup file checksum does not match its manifest: " + Path.GetFileName(path));
        }
    }

    private static async Task<BackupFileDescriptor> DescribeFileAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        return new BackupFileDescriptor(Path.GetFileName(path), info.Length, await HashFileAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<(long Length, string Hash)> CopyAndHashAsync(
        string source,
        string destination,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[bufferSize];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static async Task RunPostgreSqlToolAsync(
        string executable,
        string connectionString,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
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
        startInfo.ArgumentList.Add("--port=" + connection.Port);
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

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PostgreSQL backup tool.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("PostgreSQL backup tool failed: " + error.Result.Trim());
        }
    }

    private async Task ValidateDumpArchiveAsync(string dumpPath, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(_backupOptions.PgRestorePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add(dumpPath);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start pg_restore archive validation.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new BackupValidationException("The PostgreSQL dump is not a valid readable archive.");
        }
    }

    private static async Task EnsureSchemaAbsentAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        if (await SchemaExistsAsync(connectionString, schema, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The restore schema already exists; restore will not overwrite it.");
        }
    }

    private static async Task<bool> SchemaExistsAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new("select exists(select 1 from pg_namespace where nspname = @schema);", connection);
        command.Parameters.AddWithValue("schema", schema);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private static async Task DropSchemaAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new("drop schema if exists " + QuoteIdentifier(schema) + " cascade;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveSafePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new BackupValidationException("The manifest contains an unsafe path.");
        }

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(root, normalized));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackupValidationException("The manifest contains a path outside the backup set.");
        }

        return full;
    }

    private static void EnsureSafeBlobId(string blobId)
    {
        if (!IsIdentifier(blobId))
        {
            throw new BackupValidationException("The inventory contains an unsafe blob identifier.");
        }
    }

    private static bool IsIdentifier(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);

    private static bool IsSchemaName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string QuoteIdentifier(string value)
    {
        if (!IsSchemaName(value))
        {
            throw new BackupValidationException("The metadata schema name is unsafe.");
        }

        return "\"" + value + "\"";
    }

    private static void TryDeleteDirectory(string path)
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
}
