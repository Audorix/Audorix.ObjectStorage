using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Audorix.ObjectStorage.Api;

internal sealed class ObjectStorageApiHandler
{
    private static readonly string[] ConditionalHeaders =
    [
        "If-Match",
        "If-None-Match",
        "If-Modified-Since",
        "If-Unmodified-Since",
        "If-Range"
    ];

    private readonly IObjectStorageEngine _storage;
    private readonly IObjectStorageApiAuthorization _authorization;
    private readonly ObjectStorageApiContinuationTokenCodec _tokens;
    private readonly ObjectStorageApiOptions _options;
    private readonly ObjectStorageApiOperationAdmission _admission;

    public ObjectStorageApiHandler(
        IObjectStorageEngine storage,
        IObjectStorageApiAuthorization authorization,
        ObjectStorageApiContinuationTokenCodec tokens,
        ObjectStorageApiOperationAdmission admission,
        IOptions<ObjectStorageApiOptions> options)
    {
        _storage = storage;
        _authorization = authorization;
        _tokens = tokens;
        _admission = admission;
        _options = options.Value;
    }

    public Task BrowseAsync(HttpContext context) => ExecuteAsync(context, BrowseCoreAsync);

    public Task MetadataAsync(HttpContext context) => ExecuteAsync(context, MetadataCoreAsync);

    public Task DownloadAsync(HttpContext context) =>
        ExecuteAsync(context, value => ContentCoreAsync(value, ObjectStorageApiOperation.Download, inlineRequested: false));

    public Task ViewAsync(HttpContext context) =>
        ExecuteAsync(context, value => ContentCoreAsync(value, ObjectStorageApiOperation.View, inlineRequested: true));

    private async Task BrowseCoreAsync(HttpContext context)
    {
        string scope = RequiredQuery(context, "scope");
        string prefix = OptionalQuery(context, "prefix") ?? string.Empty;
        string? delimiter = OptionalQuery(context, "delimiter");
        if (delimiter?.Length == 0)
        {
            delimiter = null;
        }

        int pageSize = ParsePageSize(OptionalQuery(context, "pageSize"));
        ObjectStorageApiGrant grant = await AuthorizeAsync(context, ObjectStorageApiOperation.Browse, scope).ConfigureAwait(false);
        using IDisposable admission = await _admission.AcquireAsync(context.RequestAborted).ConfigureAwait(false);

        string physicalPrefix = grant.KeyPrefix + prefix;
        string? continuationToken = OptionalQuery(context, "continuationToken");
        string? afterEntry = continuationToken is null
            ? null
            : _tokens.Decode(continuationToken, scope, grant, prefix, delimiter, pageSize);

        ObjectEntryListPage page = await _storage.ListObjectEntriesAsync(
            grant.BucketName,
            physicalPrefix,
            delimiter,
            pageSize,
            afterEntry,
            context.RequestAborted).ConfigureAwait(false);

        List<ObjectStorageApiListItem> items = page.Entries.Select(entry => ToListItem(entry, grant)).ToList();
        string? nextToken = page.HasMore && page.Entries.Count > 0
            ? _tokens.Encode(scope, grant, prefix, delimiter, pageSize, page.Entries[^1].Key)
            : null;

        ApplyPrivateResponseHeaders(context.Response);
        await context.Response.WriteAsJsonAsync(
            new ObjectStorageApiBrowseResponse(scope, prefix, delimiter, items, nextToken),
            context.RequestAborted).ConfigureAwait(false);
    }

    private async Task MetadataCoreAsync(HttpContext context)
    {
        string scope = RequiredQuery(context, "scope");
        string logicalKey = RequiredQuery(context, "key");
        ObjectStorageApiGrant grant = await AuthorizeAsync(context, ObjectStorageApiOperation.Metadata, scope).ConfigureAwait(false);
        ValidateLogicalKey(logicalKey);
        using IDisposable admission = await _admission.AcquireAsync(context.RequestAborted).ConfigureAwait(false);
        ObjectInfo item = await _storage.GetObjectAsync(
            grant.BucketName,
            grant.KeyPrefix + logicalKey,
            context.RequestAborted).ConfigureAwait(false)
            ?? throw new ObjectNotFoundException(grant.BucketName, logicalKey);

        ApplyPrivateResponseHeaders(context.Response);
        await context.Response.WriteAsJsonAsync(
            new ObjectStorageApiMetadataResponse(
                scope,
                ToLogicalKey(item.Key, grant),
                item.Size,
                SafeContentType(item.ContentType),
                item.ModifiedAt,
                QuoteEtag(item.BlobId),
                FilterMetadata(item.Metadata)),
            context.RequestAborted).ConfigureAwait(false);
    }

    private async Task ContentCoreAsync(
        HttpContext context,
        ObjectStorageApiOperation operation,
        bool inlineRequested)
    {
        RejectConditionalRequests(context.Request);
        string scope = RequiredQuery(context, "scope");
        string logicalKey = RequiredQuery(context, "key");
        string? requestedFileName = OptionalQuery(context, "filename");
        if (requestedFileName?.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ObjectStorageApiRequestException("The filename contains invalid characters.");
        }

        ObjectStorageApiGrant grant = await AuthorizeAsync(context, operation, scope).ConfigureAwait(false);
        ValidateLogicalKey(logicalKey);
        using IDisposable admission = await _admission.AcquireAsync(context.RequestAborted).ConfigureAwait(false);
        string physicalKey = grant.KeyPrefix + logicalKey;
        string? rangeHeader = SingleHeader(context.Request, "Range");
        bool isHead = HttpMethods.IsHead(context.Request.Method);

        if (rangeHeader is null)
        {
            await using ObjectReadResult result = await _storage.OpenReadAsync(
                grant.BucketName,
                physicalKey,
                context.RequestAborted).ConfigureAwait(false);
            ApplyContentHeaders(context.Response, result.Object, result.Object.Size, requestedFileName, logicalKey, inlineRequested);
            context.Response.StatusCode = StatusCodes.Status200OK;
            if (!isHead)
            {
                await result.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        ObjectByteRange requestedRange = ParseRange(rangeHeader);
        await using ObjectRangeReadResult ranged = await _storage.OpenReadRangeAsync(
            grant.BucketName,
            physicalKey,
            requestedRange,
            context.RequestAborted).ConfigureAwait(false);
        ApplyContentHeaders(context.Response, ranged.Object, ranged.Range.ContentLength, requestedFileName, logicalKey, inlineRequested);
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.Headers.ContentRange = $"bytes {ranged.Range.Start}-{ranged.Range.End}/{ranged.Range.FullLength}";
        if (!isHead)
        {
            await ranged.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task<ObjectStorageApiGrant> AuthorizeAsync(
        HttpContext context,
        ObjectStorageApiOperation operation,
        string scope)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            throw new ObjectStorageApiUnauthenticatedException();
        }

        ObjectStorageApiGrant? grant = await _authorization.AuthorizeAsync(
            new ObjectStorageApiAuthorizationRequest(context, operation, scope),
            context.RequestAborted).ConfigureAwait(false);
        if (grant is null)
        {
            throw new ObjectStorageApiHiddenResourceException();
        }

        if (string.IsNullOrWhiteSpace(grant.BucketName) || grant.KeyPrefix is null)
        {
            throw new InvalidOperationException("The object-storage API authorization integration returned an invalid grant.");
        }

        return grant;
    }

    private async Task ExecuteAsync(HttpContext context, Func<HttpContext, Task> action)
    {
        try
        {
            ValidateRequestLimits(context.Request);
            await action(context).ConfigureAwait(false);
        }
        catch (ObjectStorageApiUnauthenticatedException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        catch (Exception exception) when (
            !context.Response.HasStarted
            && exception is ObjectStorageApiHiddenResourceException or ObjectNotFoundException or BucketNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (ObjectRangeNotSatisfiableException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = $"bytes */{exception.FullLength}";
            context.Response.Headers.AcceptRanges = "bytes";
        }
        catch (ObjectStorageApiRequestException exception) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new { error = "invalid_request", message = exception.Message },
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (ObjectStorageApiOverloadedException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "1";
            await context.Response.WriteAsJsonAsync(
                new { error = "service_unavailable", message = "The service is at its configured operation limit." },
                context.RequestAborted).ConfigureAwait(false);
        }
    }

    private ObjectStorageApiListItem ToListItem(ObjectListEntry entry, ObjectStorageApiGrant grant)
    {
        string logicalKey = ToLogicalKey(entry.Key, grant);
        if (entry.IsCommonPrefix)
        {
            return new("prefix", logicalKey, null, null, null, null, EmptyMetadata());
        }

        ObjectInfo item = entry.Object!;
        return new(
            "object",
            logicalKey,
            item.Size,
            SafeContentType(item.ContentType),
            item.ModifiedAt,
            QuoteEtag(item.BlobId),
            FilterMetadata(item.Metadata));
    }

    private IReadOnlyDictionary<string, string> FilterMetadata(IReadOnlyDictionary<string, string> metadata) =>
        metadata
            .Where(pair => _options.ExposedMetadataKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> EmptyMetadata() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private void ApplyContentHeaders(
        HttpResponse response,
        ObjectInfo item,
        long contentLength,
        string? requestedFileName,
        string logicalKey,
        bool inlineRequested)
    {
        string contentType = SafeContentType(item.ContentType);
        string mediaType = contentType.Split(';', 2)[0].Trim();
        bool inline = inlineRequested && _options.InlineContentTypes.Contains(mediaType);
        string fileName = SanitizeFileName(requestedFileName ?? DefaultFileName(logicalKey));
        ContentDispositionHeaderValue disposition = new(inline ? "inline" : "attachment")
        {
            FileName = AsciiFileName(fileName),
            FileNameStar = fileName
        };

        response.ContentType = contentType;
        response.ContentLength = contentLength;
        response.Headers.ETag = QuoteEtag(item.BlobId);
        response.Headers.LastModified = item.ModifiedAt.ToString("R", CultureInfo.InvariantCulture);
        response.Headers.AcceptRanges = "bytes";
        response.Headers.ContentDisposition = disposition.ToString();
        ApplyPrivateResponseHeaders(response);
    }

    private static void ApplyPrivateResponseHeaders(HttpResponse response)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "private, no-store";
        response.Headers.Pragma = "no-cache";
    }

    private static string RequiredQuery(HttpContext context, string name)
    {
        string? value = OptionalQuery(context, name);
        return string.IsNullOrEmpty(value)
            ? throw new ObjectStorageApiRequestException($"The '{name}' query parameter is required.")
            : value;
    }

    private static string? OptionalQuery(HttpContext context, string name)
    {
        if (!context.Request.Query.TryGetValue(name, out StringValues values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new ObjectStorageApiRequestException($"The '{name}' query parameter must be specified once.");
        }

        return values[0];
    }

    private static string? SingleHeader(HttpRequest request, string name)
    {
        if (!request.Headers.TryGetValue(name, out StringValues values))
        {
            return null;
        }

        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            throw new ObjectStorageApiRequestException($"The '{name}' header is invalid.");
        }

        return values[0];
    }

    private void ValidateRequestLimits(HttpRequest request)
    {
        string rawTarget = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget
            ?? request.Path + request.QueryString;
        int queryIndex = rawTarget.IndexOf('?');
        if (queryIndex >= 0 && Encoding.UTF8.GetByteCount(rawTarget[(queryIndex + 1)..]) > _options.MaxQueryStringBytes)
        {
            throw new ObjectStorageApiRequestException("The query string exceeds the configured limit.");
        }

        if (request.Headers.Count > _options.MaxHeaderCount)
        {
            throw new ObjectStorageApiRequestException("The request contains too many headers.");
        }

        int bytes = 0;
        foreach ((string name, StringValues values) in request.Headers)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(name));
            foreach (string? value in values) bytes = checked(bytes + Encoding.UTF8.GetByteCount(value ?? string.Empty));
            if (bytes > _options.MaxHeaderBytes)
            {
                throw new ObjectStorageApiRequestException("The request headers exceed the configured limit.");
            }
        }
    }

    private void ValidateLogicalKey(string key)
    {
        if (Encoding.UTF8.GetByteCount(key) > _options.MaxLogicalKeyLengthBytes
            || key.Any(static character => character == '\0' || char.IsControl(character)))
        {
            throw new ObjectStorageApiRequestException("The object key is invalid or exceeds the configured limit.");
        }
    }

    private int ParsePageSize(string? value)
    {
        int pageSize = _options.DefaultPageSize;
        if (value is not null
            && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1))
        {
            throw new ObjectStorageApiRequestException("pageSize must be a positive integer.");
        }

        if (pageSize > _options.MaximumPageSize || _options.MaximumPageSize is < 1 or > 1000)
        {
            throw new ObjectStorageApiRequestException("pageSize exceeds the configured limit.");
        }

        return pageSize;
    }

    private static ObjectByteRange ParseRange(string value)
    {
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || value.Contains(','))
        {
            throw new ObjectStorageApiRequestException("Only one bytes range is supported.");
        }

        string range = value["bytes=".Length..].Trim();
        int separator = range.IndexOf('-');
        if (separator < 0 || separator != range.LastIndexOf('-'))
        {
            throw new ObjectStorageApiRequestException("The Range header is malformed.");
        }

        string startValue = range[..separator].Trim();
        string endValue = range[(separator + 1)..].Trim();
        if (startValue.Length == 0)
        {
            return long.TryParse(endValue, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix)
                ? ObjectByteRange.FromSuffix(suffix)
                : throw new ObjectStorageApiRequestException("The Range header is malformed.");
        }

        if (!long.TryParse(startValue, NumberStyles.None, CultureInfo.InvariantCulture, out long start))
        {
            throw new ObjectStorageApiRequestException("The Range header is malformed.");
        }

        if (endValue.Length == 0)
        {
            return ObjectByteRange.FromStart(start);
        }

        if (!long.TryParse(endValue, NumberStyles.None, CultureInfo.InvariantCulture, out long end) || end < start)
        {
            throw new ObjectStorageApiRequestException("The Range header is malformed.");
        }

        return ObjectByteRange.FromStartAndEnd(start, end);
    }

    private static void RejectConditionalRequests(HttpRequest request)
    {
        if (ConditionalHeaders.Any(request.Headers.ContainsKey))
        {
            throw new ObjectStorageApiRequestException("Conditional requests are not supported by this API slice.");
        }
    }

    private static string ToLogicalKey(string physicalKey, ObjectStorageApiGrant grant)
    {
        if (!physicalKey.StartsWith(grant.KeyPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage returned an object outside the authorized key prefix.");
        }

        return physicalKey[grant.KeyPrefix.Length..];
    }

    private static string SafeContentType(string? value) =>
        value is not null && MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? parsed)
            ? parsed.ToString()
            : "application/octet-stream";

    private static string QuoteEtag(string value)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("audorix-api-etag-v1\0" + value));
        return $"\"{Convert.ToHexStringLower(digest)}\"";
    }

    private static string DefaultFileName(string key)
    {
        int separator = key.LastIndexOf('/');
        return separator >= 0 && separator < key.Length - 1 ? key[(separator + 1)..] : key;
    }

    private static string SanitizeFileName(string value)
    {
        string sanitized = new(value.Select(character => char.IsControl(character) || character is '/' or '\\' ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized;
    }

    private static string AsciiFileName(string value)
    {
        string fallback = new(value.Select(character => character is >= ' ' and <= '~' && character is not '"' and not '\\' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(fallback) ? "download" : fallback;
    }

    private sealed class ObjectStorageApiUnauthenticatedException : Exception;
    private sealed class ObjectStorageApiHiddenResourceException : Exception;
}
