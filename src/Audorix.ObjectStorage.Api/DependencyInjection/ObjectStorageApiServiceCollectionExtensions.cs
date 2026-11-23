using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Audorix.ObjectStorage.Api.DependencyInjection;

public static class ObjectStorageApiServiceCollectionExtensions
{
    public static IServiceCollection AddAudorixObjectStorageApi(
        this IServiceCollection services,
        Action<ObjectStorageApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        Microsoft.Extensions.Options.OptionsBuilder<ObjectStorageApiOptions> options = services.AddOptions<ObjectStorageApiOptions>();
        if (configure is not null) options.Configure(configure);
        options.Validate(value =>
                value.DefaultPageSize > 0
                && value.MaximumPageSize is >= 1 and <= 1000
                && value.DefaultPageSize <= value.MaximumPageSize
                && value.MaxLogicalKeyLengthBytes > 0
                && value.MaxQueryStringBytes > 0
                && value.MaxHeaderCount > 0
                && value.MaxHeaderBytes > 0
                && value.MaxContinuationTokenLength > 0
                && value.ContinuationTokenLifetime > TimeSpan.Zero
                && value.MaxConcurrentOperations > 0
                && value.MaxQueuedOperations >= 0,
            "Object storage API paging and resource limits must be valid.")
            .ValidateOnStart();

        services.AddDataProtection();
        services.TryAddSingleton<IObjectStorageApiAuthorization, DenyAllObjectStorageApiAuthorization>();
        services.TryAddSingleton<ObjectStorageApiContinuationTokenCodec>();
        services.TryAddSingleton<ObjectStorageApiHandler>();
        services.TryAddSingleton<ObjectStorageApiOperationAdmission>();
        return services;
    }
}
