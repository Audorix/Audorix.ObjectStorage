using Audorix.ObjectStorage.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Audorix.ObjectStorage.DependencyInjection;

public static class ObjectStorageServiceCollectionExtensions
{
    public static IServiceCollection AddAudorixObjectStorage(
        this IServiceCollection services,
        Action<ObjectStorageOptions> configure)
    {
        services.AddOptions<ObjectStorageOptions>()
            .Configure(configure)
            .Validate(options =>
                    !string.IsNullOrWhiteSpace(options.StorageRootPath)
                    && options.CopyBufferSize is >= 4096 and <= 4 * 1024 * 1024
                    && options.UnreferencedBlobRetention >= TimeSpan.Zero
                    && options.MaxKeyLengthBytes > 0
                    && options.MaxMetadataEntries >= 0
                    && options.MaxMetadataBytes >= 0
                    && options.MaxContentTypeLength > 0,
                "Object storage paths and resource limits must be valid.")
            .ValidateOnStart();
        services.AddSingleton<IObjectStorageEngine, FileSystemObjectStorageEngine>();
        return services;
    }

    public static IServiceCollection AddAudorixObjectStorageMetadataStore<TStore>(this IServiceCollection services)
        where TStore : class, IObjectStorageMetadataStore
    {
        services.AddSingleton<IObjectStorageMetadataStore, TStore>();
        return services;
    }
}
