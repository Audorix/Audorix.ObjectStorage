using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Audorix.ObjectStorage.Metadata;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Audorix.ObjectStorage.PostgreSql;

public sealed class PostgreSqlObjectStorageMetadataStore : IObjectStorageMetadataStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PostgreSqlObjectStorageOptions _options;

    public PostgreSqlObjectStorageMetadataStore(IOptions<PostgreSqlObjectStorageOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(options));
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string schema = QuoteIdentifier(_options.SchemaName);
        string sql = $$"""
            create schema if not exists {{schema}};

            create table if not exists {{schema}}.buckets (
                name text primary key,
                created_at timestamptz not null
            );

            create table if not exists {{schema}}.objects (
                bucket_name text not null references {{schema}}.buckets(name) on delete cascade,
                key text not null,
                blob_id text not null,
                content_type text null,
                metadata jsonb not null default '{}'::jsonb,
                size bigint not null,
                modified_at timestamptz not null,
                primary key (bucket_name, key)
            );

            create index if not exists ix_audorix_object_storage_objects_bucket_key
                on {{schema}}.objects(bucket_name, key);

            create index if not exists ix_audorix_object_storage_objects_blob
                on {{schema}}.objects(blob_id);
            """;

        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BucketInfo> CreateBucketAsync(string bucketName, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string sql = $"""
            insert into {Table("buckets")} (name, created_at)
            values (@name, @created_at)
            on conflict (name) do update set name = excluded.name
            returning name, created_at;
            """;

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("name", bucketName);
        command.Parameters.AddWithValue("created_at", createdAt);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadBucket(reader);
    }

    public async Task<BucketInfo?> GetBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"select name, created_at from {Table("buckets")} where name = @name;",
            connection);
        command.Parameters.AddWithValue("name", bucketName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadBucket(reader) : null;
    }

    public async Task<ObjectInfo> UpsertObjectAsync(ObjectInfo objectInfo, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string sql = $"""
            insert into {Table("objects")} (bucket_name, key, blob_id, content_type, metadata, size, modified_at)
            values (@bucket_name, @key, @blob_id, @content_type, @metadata, @size, @modified_at)
            on conflict (bucket_name, key) do update set
                blob_id = excluded.blob_id,
                content_type = excluded.content_type,
                metadata = excluded.metadata,
                size = excluded.size,
                modified_at = excluded.modified_at
            returning bucket_name, key, blob_id, content_type, metadata, size, modified_at;
            """;

        await using NpgsqlCommand command = new(sql, connection);
        AddObjectParameters(command, objectInfo);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadObject(reader);
    }

    public async Task<ObjectInfo?> GetObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"select bucket_name, key, blob_id, content_type, metadata, size, modified_at from {Table("objects")} where bucket_name = @bucket_name and key = @key;",
            connection);
        command.Parameters.AddWithValue("bucket_name", bucketName);
        command.Parameters.AddWithValue("key", key);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadObject(reader) : null;
    }

    public async Task<ObjectListPage> ListObjectsAsync(
        string bucketName,
        string? prefix,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        string? afterKey = DecodeContinuationToken(continuationToken);
        List<string> predicates = ["bucket_name = @bucket_name"];
        if (prefix is not null)
        {
            predicates.Add("key like @prefix");
        }

        if (afterKey is not null)
        {
            predicates.Add("key > @after_key");
        }

        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string sql = $"""
            select bucket_name, key, blob_id, content_type, metadata, size, modified_at
            from {Table("objects")}
            where {string.Join(" and ", predicates)}
            order by key
            limit @limit;
            """;

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("bucket_name", bucketName);
        command.Parameters.AddWithValue("limit", pageSize + 1);
        if (prefix is not null)
        {
            command.Parameters.AddWithValue("prefix", EscapeLike(prefix) + "%");
        }

        if (afterKey is not null)
        {
            command.Parameters.AddWithValue("after_key", afterKey);
        }

        List<ObjectInfo> objects = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objects.Add(ReadObject(reader));
        }

        string? nextToken = null;
        if (objects.Count > pageSize)
        {
            nextToken = EncodeContinuationToken(objects[pageSize - 1].Key);
            objects.RemoveAt(objects.Count - 1);
        }

        return new ObjectListPage(objects, nextToken);
    }

    public async Task<ObjectEntryListPage> ListObjectEntriesAsync(
        string bucketName,
        string prefix,
        string? delimiter,
        int maxResults,
        string? afterEntry,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string sql = $"""
            with candidates as (
                select
                    case
                        when @delimiter is not null
                             and strpos(substring(key from char_length(@prefix) + 1), @delimiter) > 0
                        then @prefix || substring(
                            substring(key from char_length(@prefix) + 1)
                            from 1 for strpos(substring(key from char_length(@prefix) + 1), @delimiter) + char_length(@delimiter) - 1)
                        else key
                    end as entry_key,
                    case
                        when @delimiter is not null
                             and strpos(substring(key from char_length(@prefix) + 1), @delimiter) > 0
                        then true
                        else false
                    end as is_prefix,
                    bucket_name, key, blob_id, content_type, metadata, size, modified_at
                from {Table("objects")}
                where bucket_name = @bucket_name
                  and key collate "C" like @prefix_pattern escape '\'
            ), deduplicated as (
                select distinct on (entry_key collate "C")
                    entry_key, is_prefix, bucket_name, key, blob_id, content_type, metadata, size, modified_at
                from candidates
                where @after_entry is null or entry_key collate "C" > @after_entry collate "C"
                order by entry_key collate "C", is_prefix desc
            )
            select entry_key, is_prefix, bucket_name, key, blob_id, content_type, metadata, size, modified_at
            from deduplicated
            order by entry_key collate "C"
            limit @limit;
            """;

        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("bucket_name", bucketName);
        command.Parameters.AddWithValue("prefix", prefix);
        command.Parameters.AddWithValue("prefix_pattern", EscapeLike(prefix) + "%");
        command.Parameters.AddWithValue("delimiter", NpgsqlDbType.Text, (object?)delimiter ?? DBNull.Value);
        command.Parameters.AddWithValue("after_entry", NpgsqlDbType.Text, (object?)afterEntry ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", maxResults + 1);

        List<ObjectListEntry> entries = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string entryKey = reader.GetString(0);
            bool isPrefix = reader.GetBoolean(1);
            ObjectInfo? item = isPrefix ? null : ReadObject(reader, offset: 2);
            entries.Add(new ObjectListEntry(entryKey, item));
        }

        bool hasMore = entries.Count > maxResults;
        if (hasMore)
        {
            entries.RemoveAt(entries.Count - 1);
        }

        return new ObjectEntryListPage(entries, hasMore);
    }

    public async Task<bool> DeleteObjectAsync(string bucketName, string key, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"delete from {Table("objects")} where bucket_name = @bucket_name and key = @key;",
            connection);
        command.Parameters.AddWithValue("bucket_name", bucketName);
        command.Parameters.AddWithValue("key", key);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<IReadOnlySet<string>> GetReferencedBlobIdsAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new($"select distinct blob_id from {Table("objects")};", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> blobIds = new(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            blobIds.Add(reader.GetString(0));
        }

        return blobIds;
    }

    public async IAsyncEnumerable<string> EnumerateReferencedBlobIdsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"select distinct blob_id collate \"C\" as blob_id from {Table("objects")} order by blob_id collate \"C\";",
            connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reader.GetString(0);
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private string Table(string name) => $"{QuoteIdentifier(_options.SchemaName)}.{QuoteIdentifier(name)}";

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Any(static c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            throw new ArgumentException("PostgreSQL identifiers must contain only letters, digits, and underscores.");
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static BucketInfo ReadBucket(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));

    private static ObjectInfo ReadObject(NpgsqlDataReader reader, int offset = 0)
    {
        string json = reader.GetString(offset + 4);
        Dictionary<string, string> metadata =
            JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? [];

        return new ObjectInfo(
            reader.GetString(offset),
            reader.GetString(offset + 1),
            reader.GetString(offset + 2),
            reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
            metadata,
            reader.GetInt64(offset + 5),
            reader.GetFieldValue<DateTimeOffset>(offset + 6));
    }

    private static void AddObjectParameters(NpgsqlCommand command, ObjectInfo objectInfo)
    {
        command.Parameters.AddWithValue("bucket_name", objectInfo.BucketName);
        command.Parameters.AddWithValue("key", objectInfo.Key);
        command.Parameters.AddWithValue("blob_id", objectInfo.BlobId);
        command.Parameters.AddWithValue("content_type", (object?)objectInfo.ContentType ?? DBNull.Value);
        command.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(objectInfo.Metadata, JsonOptions));
        command.Parameters.AddWithValue("size", objectInfo.Size);
        command.Parameters.AddWithValue("modified_at", objectInfo.ModifiedAt);
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private static string EncodeContinuationToken(string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

    private static string? DecodeContinuationToken(string? token) =>
        string.IsNullOrEmpty(token) ? null : Encoding.UTF8.GetString(Convert.FromBase64String(token));
}
