using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Audorix.ObjectStorage.Api.Routing;

public static class ObjectStorageApiEndpointRouteBuilderExtensions
{
    public static RouteGroupBuilder MapAudorixObjectStorageApi(
        this IEndpointRouteBuilder endpoints,
        string routePrefix = "/object-storage")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);
        string trimmedPrefix = routePrefix.Trim('/');
        if (trimmedPrefix.Length == 0)
        {
            throw new ArgumentException("The object-storage API must use a non-root route prefix.", nameof(routePrefix));
        }

        ObjectStorageApiOptions options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ObjectStorageApiOptions>>()
            .Value;
        RouteGroupBuilder group = endpoints.MapGroup("/" + trimmedPrefix);
        if (string.IsNullOrWhiteSpace(options.AuthorizationPolicyName))
        {
            group.RequireAuthorization();
        }
        else
        {
            group.RequireAuthorization(options.AuthorizationPolicyName);
        }

        group.MapGet("/objects", context => Handler(context).BrowseAsync(context));
        group.MapGet("/metadata", context => Handler(context).MetadataAsync(context));
        group.MapMethods("/download", ["GET", "HEAD"], context => Handler(context).DownloadAsync(context));
        group.MapMethods("/view", ["GET", "HEAD"], context => Handler(context).ViewAsync(context));
        return group;
    }

    private static ObjectStorageApiHandler Handler(Microsoft.AspNetCore.Http.HttpContext context) =>
        context.RequestServices.GetRequiredService<ObjectStorageApiHandler>();
}
