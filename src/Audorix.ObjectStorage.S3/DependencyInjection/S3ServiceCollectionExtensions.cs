using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Audorix.ObjectStorage.S3.DependencyInjection;

public static class S3ServiceCollectionExtensions
{
    public static IServiceCollection AddAudorixObjectStorageS3(
        this IServiceCollection services,
        Action<S3EndpointOptions> configure)
    {
        services.AddOptions<S3EndpointOptions>()
            .Configure(configure)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Region)
                    && Encoding.UTF8.GetByteCount(options.ContinuationTokenSigningKey) >= 32
                    && (options.MaxObjectSize is null or > 0)
                    && options.AllowedClockSkew > TimeSpan.Zero
                    && options.MaxKeyLengthBytes > 0
                    && options.MaxQueryStringBytes > 0
                    && options.MaxHeaderCount > 0
                    && options.MaxHeaderBytes > 0
                    && options.MaxContinuationTokenLength > 0
                    && options.ContinuationTokenLifetime > TimeSpan.Zero
                    && options.MaxConcurrentOperations > 0
                    && options.MaxQueuedOperations >= 0
                    && options.Credentials.Count > 0
                    && options.Credentials.All(credential =>
                        !string.IsNullOrWhiteSpace(credential.AccessKeyId)
                        && !string.IsNullOrWhiteSpace(credential.SecretAccessKey)
                        && credential.AllowedBuckets.Count > 0
                        && credential.AllowedOperations != S3Operation.None)
                    && options.Credentials.Select(credential => credential.AccessKeyId).Distinct(StringComparer.Ordinal).Count() == options.Credentials.Count,
                "S3 region, a continuation-token signing key of at least 32 bytes, and unique non-empty credential scopes are required.")
            .ValidateOnStart();
        services.AddSingleton<S3RequestHandler>();
        services.AddSingleton<S3ContinuationTokenCodec>();
        services.AddSingleton<S3OperationAdmission>();
        return services;
    }
}
