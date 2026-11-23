using Microsoft.AspNetCore.Http;

namespace Audorix.ObjectStorage.Api;

public enum ObjectStorageApiOperation
{
    Browse,
    Metadata,
    Download,
    View
}

public sealed record ObjectStorageApiGrant(string BucketName, string KeyPrefix);

public sealed record ObjectStorageApiAuthorizationRequest(
    HttpContext HttpContext,
    ObjectStorageApiOperation Operation,
    string LogicalScope);

public interface IObjectStorageApiAuthorization
{
    ValueTask<ObjectStorageApiGrant?> AuthorizeAsync(
        ObjectStorageApiAuthorizationRequest request,
        CancellationToken cancellationToken);
}

public sealed class DenyAllObjectStorageApiAuthorization : IObjectStorageApiAuthorization
{
    public ValueTask<ObjectStorageApiGrant?> AuthorizeAsync(
        ObjectStorageApiAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ObjectStorageApiGrant?>(null);
    }
}

public sealed class ObjectStorageApiOptions
{
    public int DefaultPageSize { get; set; } = 100;

    public int MaximumPageSize { get; set; } = 1000;

    public int MaxLogicalKeyLengthBytes { get; set; } = 1024;

    public int MaxQueryStringBytes { get; set; } = 8 * 1024;

    public int MaxHeaderCount { get; set; } = 100;

    public int MaxHeaderBytes { get; set; } = 32 * 1024;

    public int MaxContinuationTokenLength { get; set; } = 8 * 1024;

    public TimeSpan ContinuationTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxConcurrentOperations { get; set; } = 64;

    public int MaxQueuedOperations { get; set; }

    public string? AuthorizationPolicyName { get; set; }

    public ISet<string> ExposedMetadataKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ISet<string> InlineContentTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/avif",
        "image/bmp",
        "image/gif",
        "image/jpeg",
        "image/png",
        "image/tiff",
        "image/webp",
        "audio/aac",
        "audio/flac",
        "audio/mpeg",
        "audio/mp4",
        "audio/ogg",
        "audio/wav",
        "audio/webm",
        "video/mp4",
        "video/ogg",
        "video/quicktime",
        "video/webm",
        "text/plain"
    };
}

public sealed record ObjectStorageApiListItem(
    string Kind,
    string Key,
    long? Size,
    string? ContentType,
    DateTimeOffset? ModifiedAt,
    string? ETag,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ObjectStorageApiBrowseResponse(
    string Scope,
    string Prefix,
    string? Delimiter,
    IReadOnlyList<ObjectStorageApiListItem> Items,
    string? ContinuationToken);

public sealed record ObjectStorageApiMetadataResponse(
    string Scope,
    string Key,
    long Size,
    string ContentType,
    DateTimeOffset ModifiedAt,
    string ETag,
    IReadOnlyDictionary<string, string> Metadata);
