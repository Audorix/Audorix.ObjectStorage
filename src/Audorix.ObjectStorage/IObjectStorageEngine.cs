namespace Audorix.ObjectStorage;

public interface IObjectStorageEngine : IAsyncDisposable, IDisposable
{
    Task<BucketInfo> CreateBucketAsync(string bucketName, CancellationToken cancellationToken = default);

    Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken = default);

    Task<ObjectInfo> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default);

    Task<ObjectReadResult> OpenReadAsync(string bucketName, string key, CancellationToken cancellationToken = default);

    Task<ObjectRangeReadResult> OpenReadRangeAsync(
        string bucketName,
        string key,
        ObjectByteRange range,
        CancellationToken cancellationToken = default);

    Task<ObjectInfo?> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default);

    Task<ObjectListPage> ListObjectsAsync(
        string bucketName,
        string? prefix = null,
        int pageSize = 100,
        string? continuationToken = null,
        CancellationToken cancellationToken = default);

    Task<ObjectEntryListPage> ListObjectEntriesAsync(
        string bucketName,
        string? prefix,
        string? delimiter,
        int maxResults,
        string? afterEntry = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken = default);

    Task<int> CleanupUnreferencedBlobsAsync(CancellationToken cancellationToken = default);

    Task<IAsyncDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken = default);
}
