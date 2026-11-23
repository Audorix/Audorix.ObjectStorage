namespace Audorix.ObjectStorage.PostgreSql;

public sealed class PostgreSqlBackupOptions
{
    public string PgDumpPath { get; set; } = "pg_dump";

    public string PgRestorePath { get; set; } = "pg_restore";

    public int CopyBufferSize { get; set; } = 128 * 1024;
}
