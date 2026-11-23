namespace Audorix.ObjectStorage;

/// <summary>
/// Instance-specific storage configuration. Use one active engine instance per storage root.
/// </summary>
public sealed class ObjectStorageOptions
{
    /// <summary>
    /// Root directory for object bytes, staging files, cleanup markers, and the single-instance lock.
    /// </summary>
    public string StorageRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Delay before unreferenced blobs are eligible for cleanup. This protects active readers.
    /// </summary>
    public TimeSpan UnreferencedBlobRetention { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Size of the buffer used while streaming uploads to staging.
    /// </summary>
    public int CopyBufferSize { get; set; } = 128 * 1024;

    /// <summary>Maximum UTF-8 byte length of an object key.</summary>
    public int MaxKeyLengthBytes { get; set; } = 1024;

    /// <summary>Maximum number of metadata entries accepted for one object.</summary>
    public int MaxMetadataEntries { get; set; } = 50;

    /// <summary>Maximum combined UTF-8 byte length of metadata names and values.</summary>
    public int MaxMetadataBytes { get; set; } = 8 * 1024;

    /// <summary>Maximum character length of a stored content type.</summary>
    public int MaxContentTypeLength { get; set; } = 256;
}
