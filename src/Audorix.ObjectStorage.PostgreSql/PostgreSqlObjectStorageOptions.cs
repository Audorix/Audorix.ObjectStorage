namespace Audorix.ObjectStorage.PostgreSql;

public sealed class PostgreSqlObjectStorageOptions
{
    public string ConnectionString { get; set; } = string.Empty;

    public string SchemaName { get; set; } = "audorix_object_storage";
}
