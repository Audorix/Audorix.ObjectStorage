namespace Audorix.ObjectStorage.Metadata;

public interface IObjectStorageMetadataStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<BucketInfo> CreateBucketAsync(string bucketName, DateTimeOffset createdAt, CancellationToken cancellationToken);

    Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken);

    Task<ObjectInfo> UpsertObjectAsync(ObjectInfo objectInfo, CancellationToken cancellationToken);

    Task<ObjectInfo?> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<ObjectListPage> ListObjectsAsync(
        string bucketName,
        string? prefix,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken);

    Task<ObjectEntryListPage> ListObjectEntriesAsync(
        string bucketName,
        string prefix,
        string? delimiter,
        int maxResults,
        string? afterEntry,
        CancellationToken cancellationToken);

    Task<bool> DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> GetReferencedBlobIdsAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<string> EnumerateReferencedBlobIdsAsync(CancellationToken cancellationToken);
}
