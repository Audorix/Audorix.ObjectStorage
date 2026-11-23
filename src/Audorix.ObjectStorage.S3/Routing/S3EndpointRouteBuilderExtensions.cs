using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Audorix.ObjectStorage.S3.Routing;

public static class S3EndpointRouteBuilderExtensions
{
    private static readonly string[] Methods = ["PUT", "GET", "HEAD", "DELETE", "POST", "PATCH", "OPTIONS"];

    public static IEndpointConventionBuilder MapAudorixObjectStorageS3(
        this IEndpointRouteBuilder endpoints,
        string endpointPrefix = "/s3")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointPrefix);
        string trimmedPrefix = endpointPrefix.Trim('/');
        if (trimmedPrefix.Length == 0)
        {
            throw new ArgumentException("The S3 endpoint must use a non-root path prefix.", nameof(endpointPrefix));
        }

        string normalizedPrefix = "/" + trimmedPrefix;
        return endpoints.MapMethods(normalizedPrefix + "/{**s3Resource}", Methods, async context =>
        {
            S3RequestHandler handler = context.RequestServices.GetRequiredService<S3RequestHandler>();
            await handler.HandleAsync(context, normalizedPrefix).ConfigureAwait(false);
        });
    }
}
