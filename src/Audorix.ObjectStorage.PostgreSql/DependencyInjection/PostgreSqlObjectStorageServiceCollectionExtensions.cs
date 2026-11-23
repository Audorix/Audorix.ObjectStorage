using Audorix.ObjectStorage.Metadata;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Audorix.ObjectStorage.PostgreSql.DependencyInjection;

public static class PostgreSqlObjectStorageServiceCollectionExtensions
{
    public static IServiceCollection AddAudorixObjectStoragePostgreSql(
        this IServiceCollection services,
        Action<PostgreSqlObjectStorageOptions> configure)
    {
        services.AddOptions<PostgreSqlObjectStorageOptions>()
            .Configure(configure)
            .Validate(options =>
                    !string.IsNullOrWhiteSpace(options.ConnectionString)
                    && !string.IsNullOrWhiteSpace(options.SchemaName)
                    && Encoding.UTF8.GetByteCount(options.SchemaName) <= 63
                    && options.SchemaName.All(static character => char.IsLetterOrDigit(character) || character == '_'),
                "A connection string and a valid PostgreSQL schema identifier of at most 63 UTF-8 bytes are required.")
            .ValidateOnStart();
        services.AddSingleton<IObjectStorageMetadataStore, PostgreSqlObjectStorageMetadataStore>();
        return services;
    }

    public static IServiceCollection AddAudorixObjectStoragePostgreSqlBackup(
        this IServiceCollection services,
        Action<PostgreSqlBackupOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.AddOptions<PostgreSqlBackupOptions>().Configure(configure);
        }
        else
        {
            services.AddOptions<PostgreSqlBackupOptions>();
        }

        services.AddSingleton<PostgreSqlObjectStorageBackupService>();
        return services;
    }
}
