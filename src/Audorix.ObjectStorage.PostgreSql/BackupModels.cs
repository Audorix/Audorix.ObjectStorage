namespace Audorix.ObjectStorage.PostgreSql;

public sealed record BackupFileDescriptor(string Path, long Length, string Sha256);

public sealed record ObjectStorageBackupManifest(
    int FormatVersion,
    int MetadataSchemaVersion,
    string BackupSetId,
    string MetadataSchema,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt,
    BackupFileDescriptor DatabaseDump,
    BackupFileDescriptor Inventory,
    long ObjectCount,
    bool Complete);

public sealed record BackupInventoryEntry(
    string BackupSetId,
    string BlobId,
    string Path,
    long Length,
    string Sha256);

public sealed record BackupValidationResult(ObjectStorageBackupManifest Manifest, long ValidatedObjectCount);

public sealed class BackupValidationException(string message) : Exception(message);
