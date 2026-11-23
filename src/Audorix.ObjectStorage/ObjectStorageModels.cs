namespace Audorix.ObjectStorage;

public sealed record BucketInfo(string Name, DateTimeOffset CreatedAt);

public sealed record PutObjectRequest(
    string BucketName,
    string Key,
    Stream Content,
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ObjectInfo(
    string BucketName,
    string Key,
    string BlobId,
    string? ContentType,
    IReadOnlyDictionary<string, string> Metadata,
    long Size,
    DateTimeOffset ModifiedAt);

public sealed record ObjectReadResult(ObjectInfo Object, Stream Content) : IAsyncDisposable, IDisposable
{
    public void Dispose() => Content.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (Content is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Content.Dispose();
    }
}

public enum ObjectByteRangeKind
{
    Explicit,
    OpenEnded,
    Suffix
}

public sealed record ObjectByteRange
{
    private ObjectByteRange(ObjectByteRangeKind kind, long value, long? end)
    {
        Kind = kind;
        Value = value;
        End = end;
    }

    public ObjectByteRangeKind Kind { get; }
    public long Value { get; }
    public long? End { get; }

    public static ObjectByteRange FromStartAndEnd(long start, long end)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (end < start) throw new ArgumentOutOfRangeException(nameof(end));
        return new(ObjectByteRangeKind.Explicit, start, end);
    }

    public static ObjectByteRange FromStart(long start)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        return new(ObjectByteRangeKind.OpenEnded, start, null);
    }

    public static ObjectByteRange FromSuffix(long length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        return new(ObjectByteRangeKind.Suffix, length, null);
    }
}

public sealed record ResolvedObjectByteRange(
    long FullLength,
    long Start,
    long End,
    long ContentLength);

public sealed record ObjectRangeReadResult(
    ObjectInfo Object,
    ResolvedObjectByteRange Range,
    Stream Content) : IAsyncDisposable, IDisposable
{
    public void Dispose() => Content.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (Content is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else
            Content.Dispose();
    }
}

public sealed record ObjectListPage(IReadOnlyList<ObjectInfo> Objects, string? ContinuationToken);

public sealed record ObjectListEntry(string Key, ObjectInfo? Object)
{
    public bool IsCommonPrefix => Object is null;
}

public sealed record ObjectEntryListPage(IReadOnlyList<ObjectListEntry> Entries, bool HasMore);

public class ObjectStorageException : Exception
{
    public ObjectStorageException(string message)
        : base(message)
    {
    }

    public ObjectStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BucketNotFoundException : ObjectStorageException
{
    public BucketNotFoundException(string bucketName)
        : base($"Bucket '{bucketName}' was not found.")
    {
        BucketName = bucketName;
    }

    public string BucketName { get; }
}

public sealed class ObjectNotFoundException : ObjectStorageException
{
    public ObjectNotFoundException(string bucketName, string key)
        : base($"Object '{bucketName}/{key}' was not found.")
    {
        BucketName = bucketName;
        Key = key;
    }

    public string BucketName { get; }

    public string Key { get; }
}

public sealed class ObjectRangeNotSatisfiableException : ObjectStorageException
{
    public ObjectRangeNotSatisfiableException(long fullLength)
        : base("The requested byte range cannot be satisfied.")
    {
        FullLength = fullLength;
    }

    public long FullLength { get; }
}

public sealed class ObjectStorageMaintenanceException : ObjectStorageException
{
    public ObjectStorageMaintenanceException()
        : base("Object storage is in maintenance mode and is not accepting mutations.")
    {
    }
}
