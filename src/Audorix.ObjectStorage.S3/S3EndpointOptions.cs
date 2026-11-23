namespace Audorix.ObjectStorage.S3;

[Flags]
public enum S3Operation
{
    None = 0,
    PutObject = 1,
    GetObject = 2,
    HeadObject = 4,
    DeleteObject = 8,
    ListBucket = 16,
    AllObjectOperations = PutObject | GetObject | HeadObject | DeleteObject
}

public sealed class S3Credential
{
    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    public HashSet<string> AllowedBuckets { get; set; } = new(StringComparer.Ordinal);

    public S3Operation AllowedOperations { get; set; } = S3Operation.None;
}

public sealed class S3EndpointOptions
{
    public string Region { get; set; } = "us-east-1";

    public TimeSpan AllowedClockSkew { get; set; } = TimeSpan.FromMinutes(15);

    public string ContinuationTokenSigningKey { get; set; } = string.Empty;

    public long? MaxObjectSize { get; set; }

    public int MaxKeyLengthBytes { get; set; } = 1024;

    public int MaxQueryStringBytes { get; set; } = 8 * 1024;

    public int MaxHeaderCount { get; set; } = 100;

    public int MaxHeaderBytes { get; set; } = 32 * 1024;

    public int MaxContinuationTokenLength { get; set; } = 8 * 1024;

    public TimeSpan ContinuationTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxConcurrentOperations { get; set; } = 64;

    public int MaxQueuedOperations { get; set; }

    public List<S3Credential> Credentials { get; set; } = [];
}
