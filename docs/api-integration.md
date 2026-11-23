# Authenticated ASP.NET Core API

`Audorix.ObjectStorage.Api.dll` maps authenticated application endpoints into an existing ASP.NET Core pipeline. It creates no listener, background service, identity database, or credentials. The embedded backend delegates to `IObjectStorageEngine`; object bytes remain filesystem files and metadata remains provider-based.

## Host integration

Register the core, metadata provider, the API, and the application's implementation of `IObjectStorageApiAuthorization`:

```csharp
builder.Services
    .AddAudorixObjectStorage(options => options.StorageRootPath = storageRoot)
    .AddAudorixObjectStoragePostgreSql(options => options.ConnectionString = connectionString)
    .AddAudorixObjectStorageApi(options =>
    {
        options.AuthorizationPolicyName = "StorageUsers";
        options.DefaultPageSize = 100;
        options.MaximumPageSize = 500;
        options.ExposedMetadataKeys.Add("document-kind");
    });

builder.Services.AddSingleton<IObjectStorageApiAuthorization, ApplicationStorageAuthorization>();

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapAudorixObjectStorageApi("/api/storage");
```

The host keeps its existing authentication schemes and policies. The library adds no identity system and requires authorization on the mapped route group. If no application authorization implementation is registered, `DenyAllObjectStorageApiAuthorization` denies access.

An authorization implementation maps a logical scope to physical storage only after checking trusted host identity:

```csharp
public sealed class ApplicationStorageAuthorization : IObjectStorageApiAuthorization
{
    public ValueTask<ObjectStorageApiGrant?> AuthorizeAsync(
        ObjectStorageApiAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        string? tenant = request.HttpContext.User.FindFirst("tenant_id")?.Value;
        bool allowed = tenant is not null && HasPermission(request.HttpContext.User, request.Operation);
        return ValueTask.FromResult<ObjectStorageApiGrant?>(allowed
            ? new ObjectStorageApiGrant("documents", $"tenants/{tenant}/")
            : null);
    }
}
```

Do not accept a tenant ID, bucket, or physical prefix from query parameters or untrusted headers. Return a grant only after checking the distinct `Browse`, `Metadata`, `Download`, or `View` operation. Authorization runs again on every paginated request; continuation tokens never grant access.

Authenticated denial, an unavailable logical scope, and a missing bucket or object all return `404`. This deliberately hides cross-tenant existence. Anonymous requests return `401`. Invalid request syntax returns `400`.

## Routes

With `/api/storage` as the configured prefix:

| Method | Route | Query | Result |
| --- | --- | --- | --- |
| GET | `/objects` | `scope`, optional `prefix`, `delimiter`, `pageSize`, `continuationToken` | Bounded JSON page of objects and common prefixes. |
| GET | `/metadata` | `scope`, `key` | JSON metadata for one object. |
| GET, HEAD | `/download` | `scope`, `key`, optional `filename` | Attachment response. |
| GET, HEAD | `/view` | `scope`, `key`, optional `filename` | Inline response only for an allowed media type; otherwise attachment. |

Keys and prefixes are query values so catch-all route decoding cannot alter their identity. Encode them as normal URI query values. Unicode, spaces, percent signs, `_`, `+`, `#`, and `?` are retained after query decoding. SQL wildcard characters are treated literally by the PostgreSQL provider.

Browse DTOs expose logical keys, entry kind, size, content type, modification time, opaque ETag, and only custom metadata keys in `ExposedMetadataKeys`. The default allow-list is empty. Bucket names, physical authorization prefixes, blob IDs, filesystem paths, database details, and credentials are not returned. Listing asks the provider for one bounded page and never materializes the full scope.

Continuation tokens are encrypted and authenticated with ASP.NET Core Data Protection. They are bound to logical scope, resolved bucket/prefix, requested prefix, delimiter, and page size. Persist the Data Protection key ring in a directory writable by the application identity and outside replace-on-publish directories. Losing or rotating away all keys invalidates outstanding tokens. A shared key ring does not make local filesystem blobs multi-node capable.

## Content policy

Full and single-range content is streamed from the same committed-object read result. Supported ranges are explicit (`bytes=0-99`), open-ended (`bytes=100-`), and suffix (`bytes=-100`). Valid ranges return `206`; unsatisfiable ranges return `416` with `Content-Range: bytes */length`. Malformed, non-byte, reversed, and multiple ranges return `400`. Ranged HEAD returns the same headers as ranged GET without a body.

Conditional headers (`If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`, and `If-Range`) return `400` in this slice; they are not silently interpreted. Responses set `Content-Length`, `Content-Type`, `Content-Disposition`, `ETag`, `Last-Modified`, `Accept-Ranges: bytes`, `X-Content-Type-Options: nosniff`, and `Cache-Control: private, no-store`. No CORS policy is added.

Downloads always use `attachment`. Views use `inline` only when the parsed stored content type is in `InlineContentTypes`. Defaults include PDF, selected raster images, audio, video, and plain text. HTML, SVG, Markdown, executable content, malformed types, and unknown types are attachments. Hosts can remove entries but should expand the list only after a security review. Filename extensions never decide inline eligibility.

The optional `filename` is rejected when it contains CR or LF. Content-Disposition uses an ASCII fallback plus RFC 5987 `filename*` encoding for Unicode. A filename derived from an object key is stripped to its final segment and control characters are replaced.

Request cancellation flows into storage reads and response copying. Read results are disposed on success or failure. Existing active-reader tracking preserves the opened blob during concurrent overwrite, deletion, and cleanup.

## Hosting notes

The application identity needs the filesystem permissions described in the main README plus write access to the configured Data Protection key directory. Application recycling closes active responses; clients must retry. Tokens survive recycling only while their Data Protection keys remain available.

The storage root remains single-instance. PostgreSQL metadata and a shared Data Protection key ring do not turn node-local files into shared blob storage. Browser PDF/image/audio/video rendering has not been exercised in this slice; HTTP byte delivery and disposition policy are verified, but browser viewer behavior is not claimed.
