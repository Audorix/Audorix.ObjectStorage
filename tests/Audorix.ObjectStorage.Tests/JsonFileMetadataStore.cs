using System.Text;
using System.Text.Json;
using Audorix.ObjectStorage.Metadata;

namespace Audorix.ObjectStorage.Tests;

internal sealed class JsonFileMetadataStore : IObjectStorageMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileMetadataStore(string path)
    {
        _path = path;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path))
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new State(), JsonOptions), Encoding.UTF8);
        }

        return Task.CompletedTask;
    }

    public async Task<BucketInfo> CreateBucketAsync(string bucketName, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        return await MutateAsync(state =>
        {
            if (!state.Buckets.TryGetValue(bucketName, out BucketInfo? bucket))
            {
                bucket = new BucketInfo(bucketName, createdAt);
                state.Buckets[bucketName] = bucket;
            }

            return bucket;
        }, cancellationToken);
    }

    public async Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        State state = await ReadAsync(cancellationToken);
        return state.Buckets.GetValueOrDefault(bucketName);
    }

    public async Task<ObjectInfo> UpsertObjectAsync(ObjectInfo objectInfo, CancellationToken cancellationToken)
    {
        return await MutateAsync(state =>
        {
            state.Objects[MakeKey(objectInfo.BucketName, objectInfo.Key)] = objectInfo;
            return objectInfo;
        }, cancellationToken);
    }

    public async Task<ObjectInfo?> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        State state = await ReadAsync(cancellationToken);
        return state.Objects.GetValueOrDefault(MakeKey(bucketName, key));
    }

    public async Task<ObjectListPage> ListObjectsAsync(
        string bucketName,
        string? prefix,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        State state = await ReadAsync(cancellationToken);
        string? after = Decode(continuationToken);
        List<ObjectInfo> all = state.Objects.Values
            .Where(o => string.Equals(o.BucketName, bucketName, StringComparison.Ordinal))
            .Where(o => prefix is null || o.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Where(o => after is null || string.CompareOrdinal(o.Key, after) > 0)
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .Take(pageSize + 1)
            .ToList();

        string? next = null;
        if (all.Count > pageSize)
        {
            next = Encode(all[pageSize - 1].Key);
            all.RemoveAt(all.Count - 1);
        }

        return new ObjectListPage(all, next);
    }

    public async Task<ObjectEntryListPage> ListObjectEntriesAsync(
        string bucketName,
        string prefix,
        string? delimiter,
        int maxResults,
        string? afterEntry,
        CancellationToken cancellationToken)
    {
        State state = await ReadAsync(cancellationToken);
        Utf8Comparer comparer = Utf8Comparer.Instance;
        List<ObjectListEntry> entries = state.Objects.Values
            .Where(item => string.Equals(item.BucketName, bucketName, StringComparison.Ordinal))
            .Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => ToEntry(item, prefix, delimiter))
            .GroupBy(item => item.Key, comparer)
            .Select(group => group.First())
            .Where(item => afterEntry is null || comparer.Compare(item.Key, afterEntry) > 0)
            .OrderBy(item => item.Key, comparer)
            .Take(maxResults + 1)
            .ToList();

        bool hasMore = entries.Count > maxResults;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        return new ObjectEntryListPage(entries, hasMore);
    }

    public async Task<bool> DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        return await MutateAsync(state => state.Objects.Remove(MakeKey(bucketName, key)), cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetReferencedBlobIdsAsync(CancellationToken cancellationToken)
    {
        State state = await ReadAsync(cancellationToken);
        return state.Objects.Values.Select(o => o.BlobId).ToHashSet(StringComparer.Ordinal);
    }

    public async IAsyncEnumerable<string> EnumerateReferencedBlobIdsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        State state = await ReadAsync(cancellationToken);
        foreach (string blobId in state.Objects.Values.Select(item => item.BlobId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return blobId;
        }
    }

    private async Task<T> MutateAsync<T>(Func<State, T> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            State state = await ReadUnlockedAsync(cancellationToken);
            T result = mutate(state);
            await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<State> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<State> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new State();
        }

        string json = await File.ReadAllTextAsync(_path, Encoding.UTF8, cancellationToken);
        return JsonSerializer.Deserialize<State>(json, JsonOptions) ?? new State();
    }

    private static string MakeKey(string bucketName, string key) => bucketName + "\n" + key;

    private static ObjectListEntry ToEntry(ObjectInfo item, string prefix, string? delimiter)
    {
        if (delimiter is null)
        {
            return new ObjectListEntry(item.Key, item);
        }

        string suffix = item.Key[prefix.Length..];
        int index = suffix.IndexOf(delimiter, StringComparison.Ordinal);
        return index < 0
            ? new ObjectListEntry(item.Key, item)
            : new ObjectListEntry(prefix + suffix[..(index + delimiter.Length)], null);
    }

    private static string Encode(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

    private static string? Decode(string? token) =>
        token is null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(token));

    private sealed class State
    {
        public Dictionary<string, BucketInfo> Buckets { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, ObjectInfo> Objects { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class Utf8Comparer : IComparer<string>, IEqualityComparer<string>
    {
        public static Utf8Comparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        }

        public bool Equals(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);

        public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(value);
    }
}
