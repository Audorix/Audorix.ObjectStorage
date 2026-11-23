using System.Collections.Concurrent;
using System.Text;
using Audorix.ObjectStorage.Metadata;
using Microsoft.Extensions.Options;

namespace Audorix.ObjectStorage;

public sealed class FileSystemObjectStorageEngine : IObjectStorageEngine
{
    private const string LockFileName = "audorix.objectstorage.lock";
    private readonly IObjectStorageMetadataStore _metadataStore;
    private readonly ObjectStorageOptions _options;
    private readonly string _blobsPath;
    private readonly string _stagingPath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mutationGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeReaders = new(StringComparer.Ordinal);
    private readonly Lazy<Task> _initializeTask;
    private readonly object _maintenanceSync = new();
    private readonly object _readerSync = new();
    private int _activeMutations;
    private bool _maintenanceActive;
    private TaskCompletionSource? _mutationsDrained;
    private FileStream? _lockFile;
    private bool _disposed;

    public FileSystemObjectStorageEngine(
        IObjectStorageMetadataStore metadataStore,
        IOptions<ObjectStorageOptions> options)
    {
        _metadataStore = metadataStore;
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.StorageRootPath))
        {
            throw new ArgumentException("Storage root path is required.", nameof(options));
        }

        _blobsPath = Path.Combine(_options.StorageRootPath, "blobs");
        _stagingPath = Path.Combine(_options.StorageRootPath, "staging");
        _initializeTask = new Lazy<Task>(() => InitializeCoreAsync(CancellationToken.None));
    }

    public async Task<BucketInfo> CreateBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ValidateName(bucketName, nameof(bucketName));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using IDisposable mutation = BeginMutation();
        return await _metadataStore.CreateBucketAsync(bucketName, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken = default)
    {
        ValidateName(bucketName, nameof(bucketName));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _metadataStore.GetBucketAsync(bucketName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObjectInfo> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(request.BucketName, nameof(request.BucketName));
        ValidateKey(request.Key);
        ValidateObjectProperties(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using IDisposable mutation = BeginMutation();

        if (await _metadataStore.GetBucketAsync(request.BucketName, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new BucketNotFoundException(request.BucketName);
        }

        SemaphoreSlim gate = GetMutationGate(request.BucketName, request.Key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingFile = null;
        try
        {
            string blobId = CreateBlobId();
            stagingFile = Path.Combine(_stagingPath, blobId + ".staging");
            string blobFile = GetBlobPath(blobId);
            long size;

            await using (FileStream destination = new(
                stagingFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                _options.CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await request.Content.CopyToAsync(destination, _options.CopyBufferSize, cancellationToken).ConfigureAwait(false);
                size = destination.Length;
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(stagingFile, blobFile);
            stagingFile = null;

            ObjectInfo objectInfo = new(
                request.BucketName,
                request.Key,
                blobId,
                request.ContentType,
                new Dictionary<string, string>(request.Metadata ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                size,
                DateTimeOffset.UtcNow);

            return await _metadataStore.UpsertObjectAsync(objectInfo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            if (stagingFile is not null)
            {
                TryDeleteFile(stagingFile);
            }
        }
    }

    public async Task<ObjectReadResult> OpenReadAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        ValidateName(bucketName, nameof(bucketName));
        ValidateKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        SemaphoreSlim gate = GetMutationGate(bucketName, key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectInfo objectInfo = await _metadataStore.GetObjectAsync(bucketName, key, cancellationToken).ConfigureAwait(false)
                ?? throw new ObjectNotFoundException(bucketName, key);
            return new ObjectReadResult(objectInfo, OpenBlob(objectInfo, 0));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ObjectRangeReadResult> OpenReadRangeAsync(
        string bucketName,
        string key,
        ObjectByteRange range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);
        ValidateName(bucketName, nameof(bucketName));
        ValidateKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        SemaphoreSlim gate = GetMutationGate(bucketName, key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectInfo objectInfo = await _metadataStore.GetObjectAsync(bucketName, key, cancellationToken).ConfigureAwait(false)
                ?? throw new ObjectNotFoundException(bucketName, key);
            ResolvedObjectByteRange resolved = ResolveRange(range, objectInfo.Size);
            Stream tracked = OpenBlob(objectInfo, resolved.Start);
            return new ObjectRangeReadResult(objectInfo, resolved, new BoundedReadStream(tracked, resolved.ContentLength));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ObjectInfo?> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        ValidateName(bucketName, nameof(bucketName));
        ValidateKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _metadataStore.GetObjectAsync(bucketName, key, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObjectListPage> ListObjectsAsync(
        string bucketName,
        string? prefix = null,
        int pageSize = 100,
        string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(bucketName, nameof(bucketName));
        if (pageSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 1000.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (await _metadataStore.GetBucketAsync(bucketName, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new BucketNotFoundException(bucketName);
        }

        return await _metadataStore.ListObjectsAsync(bucketName, prefix, pageSize, continuationToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObjectEntryListPage> ListObjectEntriesAsync(
        string bucketName,
        string? prefix,
        string? delimiter,
        int maxResults,
        string? afterEntry = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(bucketName, nameof(bucketName));
        if (maxResults is < 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), "Maximum results must be between 0 and 1000.");
        }

        if (delimiter is not null && delimiter.Length == 0)
        {
            throw new ArgumentException("Delimiter cannot be empty.", nameof(delimiter));
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (await _metadataStore.GetBucketAsync(bucketName, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new BucketNotFoundException(bucketName);
        }

        if (maxResults == 0)
        {
            return new ObjectEntryListPage([], HasMore: false);
        }

        return await _metadataStore.ListObjectEntriesAsync(
            bucketName,
            prefix ?? string.Empty,
            delimiter,
            maxResults,
            afterEntry,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default)
    {
        ValidateName(bucketName, nameof(bucketName));
        ValidateKey(key);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using IDisposable mutation = BeginMutation();
        SemaphoreSlim gate = GetMutationGate(bucketName, key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _metadataStore.DeleteObjectAsync(bucketName, key, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> CleanupUnreferencedBlobsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using IDisposable mutation = BeginMutation();
        IReadOnlySet<string> referenced = await _metadataStore.GetReferencedBlobIdsAsync(cancellationToken).ConfigureAwait(false);
        int removed = 0;
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(_options.UnreferencedBlobRetention);

        foreach (string blobPath in Directory.EnumerateFiles(_blobsPath, "*.blob", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string blobId = Path.GetFileNameWithoutExtension(blobPath);
            if (referenced.Contains(blobId))
            {
                continue;
            }

            DateTimeOffset modified = File.GetLastWriteTimeUtc(blobPath);
            if (modified > cutoff)
            {
                continue;
            }

            bool deleted;
            lock (_readerSync)
            {
                deleted = !_activeReaders.ContainsKey(blobId) && TryDeleteFile(blobPath);
            }
            if (deleted)
            {
                removed++;
            }
        }

        foreach (string stagingPath in Directory.EnumerateFiles(_stagingPath, "*.staging", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset modified = File.GetLastWriteTimeUtc(stagingPath);
            if (modified <= cutoff && TryDeleteFile(stagingPath))
            {
                removed++;
            }
        }

        return removed;
    }

    public async Task<IAsyncDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        Task drainTask;
        lock (_maintenanceSync)
        {
            if (_maintenanceActive)
            {
                throw new ObjectStorageMaintenanceException();
            }

            _maintenanceActive = true;
            if (_activeMutations == 0)
            {
                drainTask = Task.CompletedTask;
            }
            else
            {
                _mutationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drainTask = _mutationsDrained.Task;
            }
        }

        try
        {
            await drainTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new MaintenanceLease(this);
        }
        catch
        {
            ExitMaintenance();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lockFile?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _initializeTask.Value.ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StorageRootPath);
        Directory.CreateDirectory(_blobsPath);
        Directory.CreateDirectory(_stagingPath);
        string lockPath = Path.Combine(_options.StorageRootPath, LockFileName);
        try
        {
            _lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new ObjectStorageException(
                "Another Audorix.ObjectStorage engine instance is already active for this storage root.",
                ex);
        }

        await _metadataStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private SemaphoreSlim GetMutationGate(string bucketName, string key) =>
        _mutationGates.GetOrAdd(bucketName + "\n" + key, _ => new SemaphoreSlim(1, 1));

    private IDisposable BeginMutation()
    {
        lock (_maintenanceSync)
        {
            if (_maintenanceActive)
            {
                throw new ObjectStorageMaintenanceException();
            }

            _activeMutations++;
            return new MutationLease(this);
        }
    }

    private void EndMutation()
    {
        TaskCompletionSource? drained = null;
        lock (_maintenanceSync)
        {
            _activeMutations--;
            if (_activeMutations == 0 && _maintenanceActive)
            {
                drained = _mutationsDrained;
                _mutationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private void ExitMaintenance()
    {
        lock (_maintenanceSync)
        {
            _maintenanceActive = false;
            _mutationsDrained = null;
        }
    }

    private string GetBlobPath(string blobId) => Path.Combine(_blobsPath, blobId + ".blob");

    private Stream OpenBlob(ObjectInfo objectInfo, long offset)
    {
        lock (_readerSync)
        {
            AddActiveReader(objectInfo.BlobId);
            try
            {
                FileStream stream = new(
                    GetBlobPath(objectInfo.BlobId),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    _options.CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
                stream.Seek(offset, SeekOrigin.Begin);
                return new TrackedReadStream(stream, () => RemoveActiveReader(objectInfo.BlobId));
            }
            catch
            {
                RemoveActiveReader(objectInfo.BlobId);
                throw;
            }
        }
    }

    private static ResolvedObjectByteRange ResolveRange(ObjectByteRange range, long fullLength)
    {
        if (fullLength == 0) throw new ObjectRangeNotSatisfiableException(fullLength);
        long start;
        long end;
        switch (range.Kind)
        {
            case ObjectByteRangeKind.Explicit:
                start = range.Value;
                if (start >= fullLength) throw new ObjectRangeNotSatisfiableException(fullLength);
                end = Math.Min(range.End!.Value, fullLength - 1);
                break;
            case ObjectByteRangeKind.OpenEnded:
                start = range.Value;
                if (start >= fullLength) throw new ObjectRangeNotSatisfiableException(fullLength);
                end = fullLength - 1;
                break;
            case ObjectByteRangeKind.Suffix:
                if (range.Value == 0) throw new ObjectRangeNotSatisfiableException(fullLength);
                long selected = Math.Min(range.Value, fullLength);
                start = fullLength - selected;
                end = fullLength - 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(range));
        }

        return new(fullLength, start, end, checked(end - start + 1));
    }

    private static string CreateBlobId() => Guid.NewGuid().ToString("N");

    private static void ValidateName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }
    }

    private void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Object key is required.", nameof(key));
        }

        if (Encoding.UTF8.GetByteCount(key) > _options.MaxKeyLengthBytes)
        {
            throw new ArgumentException("Object key exceeds the configured UTF-8 length limit.", nameof(key));
        }

        if (key.Any(static character => character == '\0' || char.IsControl(character)))
        {
            throw new ArgumentException("Object key contains a null or control character.", nameof(key));
        }
    }

    private void ValidateObjectProperties(PutObjectRequest request)
    {
        if (request.ContentType is { } contentType
            && (contentType.Length > _options.MaxContentTypeLength
                || contentType.Any(static character => character == '\0' || character is '\r' or '\n')))
        {
            throw new ArgumentException("Content type is invalid or exceeds the configured limit.", nameof(request));
        }

        IReadOnlyDictionary<string, string> metadata = request.Metadata
            ?? new Dictionary<string, string>();
        if (metadata.Count > _options.MaxMetadataEntries)
        {
            throw new ArgumentException("Metadata entry count exceeds the configured limit.", nameof(request));
        }

        int metadataBytes = 0;
        foreach ((string name, string value) in metadata)
        {
            if (string.IsNullOrWhiteSpace(name)
                || name.Any(static character => character == '\0' || char.IsControl(character))
                || value.Any(static character => character == '\0' || character is '\r' or '\n'))
            {
                throw new ArgumentException("Metadata names and values must be non-empty and header-safe.", nameof(request));
            }

            metadataBytes = checked(metadataBytes + Encoding.UTF8.GetByteCount(name) + Encoding.UTF8.GetByteCount(value));
            if (metadataBytes > _options.MaxMetadataBytes)
            {
                throw new ArgumentException("Metadata exceeds the configured UTF-8 length limit.", nameof(request));
            }
        }
    }

    private void AddActiveReader(string blobId) =>
        _activeReaders.AddOrUpdate(blobId, 1, (_, current) => current + 1);

    private void RemoveActiveReader(string blobId)
    {
        lock (_readerSync)
        {
            _activeReaders.AddOrUpdate(blobId, 0, (_, current) => Math.Max(0, current - 1));
            if (_activeReaders.TryGetValue(blobId, out int current) && current <= 0)
                _activeReaders.TryRemove(blobId, out _);
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class MutationLease(FileSystemObjectStorageEngine owner) : IDisposable
    {
        private FileSystemObjectStorageEngine? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndMutation();
    }

    private sealed class MaintenanceLease(FileSystemObjectStorageEngine owner) : IAsyncDisposable
    {
        private FileSystemObjectStorageEngine? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitMaintenance();
            return ValueTask.CompletedTask;
        }
    }
}
