using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http.Features;

namespace Audorix.ObjectStorage.S3;

internal sealed class S3RequestHandler
{
    private readonly IObjectStorageEngine _storage;
    private readonly SigV4Authenticator _authenticator;
    private readonly S3ContinuationTokenCodec _continuationTokens;
    private readonly ILogger<S3RequestHandler> _logger;
    private readonly S3EndpointOptions _options;
    private readonly S3OperationAdmission _admission;
    private readonly long? _maxObjectSize;

    public S3RequestHandler(
        IObjectStorageEngine storage,
        IOptions<S3EndpointOptions> options,
        S3ContinuationTokenCodec continuationTokens,
        S3OperationAdmission admission,
        ILogger<S3RequestHandler> logger)
    {
        _storage = storage;
        _authenticator = new SigV4Authenticator(options);
        _continuationTokens = continuationTokens;
        _admission = admission;
        _logger = logger;
        _options = options.Value;
        _maxObjectSize = options.Value.MaxObjectSize;
    }

    public async Task HandleAsync(HttpContext context, string endpointPrefix)
    {
        string requestId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        context.Response.Headers["x-amz-request-id"] = requestId;
        context.Response.Headers["x-amz-id-2"] = requestId;

        try
        {
            ValidateRequestLimits(context.Request, endpointPrefix);
            ConfigureRequestBodyLimit(context);
            (string bucket, string? key) = ParseResource(context.Request, endpointPrefix);
            S3Operation operation = GetOperation(context.Request, key);
            S3AuthenticatedRequest authenticated = _authenticator.Authenticate(context.Request, operation == S3Operation.PutObject);
            Authorize(authenticated.Credential, bucket, operation);
            ValidateQuery(context.Request, operation);
            ValidateFeatureHeaders(context.Request, operation);
            using IDisposable admission = await _admission.AcquireAsync(context.RequestAborted).ConfigureAwait(false);

            BucketInfo? existingBucket = await _storage.GetBucketAsync(bucket, context.RequestAborted).ConfigureAwait(false);
            if (existingBucket is null)
            {
                throw new S3ProtocolException(404, "NoSuchBucket", "The specified bucket does not exist.");
            }

            switch (operation)
            {
                case S3Operation.PutObject:
                    await PutObjectAsync(context, authenticated.PayloadHash, bucket, key!).ConfigureAwait(false);
                    break;
                case S3Operation.GetObject:
                    await GetObjectAsync(context, bucket, key!).ConfigureAwait(false);
                    break;
                case S3Operation.HeadObject:
                    await HeadObjectAsync(context, bucket, key!).ConfigureAwait(false);
                    break;
                case S3Operation.DeleteObject:
                    await DeleteObjectAsync(context, bucket, key!).ConfigureAwait(false);
                    break;
                case S3Operation.ListBucket:
                    await ListObjectsV2Async(context, bucket, authenticated.Credential.AccessKeyId).ConfigureAwait(false);
                    break;
                default:
                    throw new S3ProtocolException(501, "NotImplemented", "The requested operation is not implemented.");
            }
        }
        catch (S3ProtocolException exception)
        {
            await WriteErrorAsync(context, exception.StatusCode, exception.Code, exception.Message, requestId).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException)
        {
            await WriteErrorAsync(context, 404, "NoSuchKey", "The specified key does not exist.", requestId).ConfigureAwait(false);
        }
        catch (ObjectRangeNotSatisfiableException exception)
        {
            await WriteErrorAsync(context, 416, "InvalidRange", exception.Message, requestId, exception.FullLength).ConfigureAwait(false);
        }
        catch (ObjectStorageMaintenanceException exception)
        {
            await WriteErrorAsync(context, 503, "ServiceUnavailable", exception.Message, requestId).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteErrorAsync(context, 413, "EntityTooLarge", "The proposed upload exceeds the configured object-size limit.", requestId).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await WriteErrorAsync(context, 400, "InvalidArgument", "A request value is invalid or exceeds a configured limit.", requestId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            _logger.LogError(exception, "Unhandled S3 endpoint failure for method {Method}.", context.Request.Method);
            await WriteErrorAsync(context, 500, "InternalError", "The service encountered an internal error.", requestId).ConfigureAwait(false);
        }
    }

    private void ConfigureRequestBodyLimit(HttpContext context)
    {
        if (context.Request.Method != "PUT")
        {
            return;
        }

        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = _maxObjectSize;
        }
    }

    private async Task PutObjectAsync(HttpContext context, string payloadHash, string bucket, string key)
    {
        Dictionary<string, string> metadata = context.Request.Headers
            .Where(header => header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                header => header.Key["x-amz-meta-".Length..],
                header => header.Value.ToString(),
                StringComparer.OrdinalIgnoreCase);

        await using HashValidatingReadStream content = new(
            context.Request.Body,
            payloadHash,
            context.Request.Headers["Content-MD5"].ToString());
        ObjectInfo result = await _storage.PutObjectAsync(
            new PutObjectRequest(bucket, key, content, context.Request.ContentType, metadata),
            context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ETag = QuoteEtag(result.BlobId);
        context.Response.ContentLength = 0;
    }

    private async Task GetObjectAsync(HttpContext context, string bucket, string key)
    {
        context.Response.Headers.AcceptRanges = "bytes";
        string rangeHeader = context.Request.Headers.Range.ToString();
        if (rangeHeader.Length == 0)
        {
            await using ObjectReadResult result = await _storage.OpenReadAsync(bucket, key, context.RequestAborted).ConfigureAwait(false);
            ApplyObjectHeaders(context.Response, result.Object);
            context.Response.StatusCode = StatusCodes.Status200OK;
            await result.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        ObjectByteRange range = ParseRange(rangeHeader);
        await using ObjectRangeReadResult ranged = await _storage.OpenReadRangeAsync(bucket, key, range, context.RequestAborted).ConfigureAwait(false);
        ApplyObjectHeaders(context.Response, ranged.Object, ranged.Range.ContentLength);
        context.Response.StatusCode = StatusCodes.Status206PartialContent;
        context.Response.Headers.ContentRange = $"bytes {ranged.Range.Start}-{ranged.Range.End}/{ranged.Range.FullLength}";
        await ranged.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HeadObjectAsync(HttpContext context, string bucket, string key)
    {
        ObjectInfo result = await _storage.GetObjectAsync(bucket, key, context.RequestAborted).ConfigureAwait(false)
            ?? throw new ObjectNotFoundException(bucket, key);
        ApplyObjectHeaders(context.Response, result);
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.StatusCode = StatusCodes.Status200OK;
    }

    private async Task DeleteObjectAsync(HttpContext context, string bucket, string key)
    {
        await _storage.DeleteObjectAsync(bucket, key, context.RequestAborted).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task ListObjectsV2Async(HttpContext context, string bucket, string accessKeyId)
    {
        string prefix = context.Request.Query["prefix"].ToString();
        string? delimiter = NullIfEmpty(context.Request.Query["delimiter"].ToString());
        string? encodingType = NullIfEmpty(context.Request.Query["encoding-type"].ToString());
        if (encodingType is not null && !string.Equals(encodingType, "url", StringComparison.Ordinal))
        {
            throw new S3ProtocolException(400, "InvalidArgument", "encoding-type must be 'url' when specified.");
        }

        int maxKeys = 1000;
        string maxKeysValue = context.Request.Query["max-keys"].ToString();
        if (maxKeysValue.Length > 0
            && (!int.TryParse(maxKeysValue, NumberStyles.None, CultureInfo.InvariantCulture, out maxKeys)
                || maxKeys is < 0 or > 1000))
        {
            throw new S3ProtocolException(400, "InvalidArgument", "max-keys must be an integer between 0 and 1000.");
        }

        string? continuationToken = NullIfEmpty(context.Request.Query["continuation-token"].ToString());
        if (continuationToken?.Length > _options.MaxContinuationTokenLength)
        {
            throw new S3ProtocolException(400, "InvalidArgument", "The continuation token exceeds the configured limit.");
        }
        string? startAfter = NullIfEmpty(context.Request.Query["start-after"].ToString());
        string? afterEntry = continuationToken is null
            ? startAfter
            : _continuationTokens.Decode(continuationToken, bucket, prefix, delimiter, encodingType, accessKeyId);

        ObjectEntryListPage page = await _storage.ListObjectEntriesAsync(
            bucket,
            prefix,
            delimiter,
            maxKeys,
            afterEntry,
            context.RequestAborted).ConfigureAwait(false);

        string? nextToken = page.HasMore && page.Entries.Count > 0
            ? _continuationTokens.Encode(bucket, prefix, delimiter, encodingType, page.Entries[^1].Key, accessKeyId)
            : null;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/xml";
        StringBuilder body = new();
        using (XmlWriter writer = XmlWriter.Create(body, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("ListBucketResult", "http://s3.amazonaws.com/doc/2006-03-01/");
            writer.WriteElementString("Name", bucket);
            writer.WriteElementString("Prefix", EncodeListingValue(prefix, encodingType));
            if (delimiter is not null)
            {
                writer.WriteElementString("Delimiter", EncodeListingValue(delimiter, encodingType));
            }

            writer.WriteElementString("KeyCount", page.Entries.Count.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("MaxKeys", maxKeys.ToString(CultureInfo.InvariantCulture));
            writer.WriteElementString("IsTruncated", page.HasMore ? "true" : "false");
            if (encodingType is not null)
            {
                writer.WriteElementString("EncodingType", encodingType);
            }

            if (continuationToken is not null)
            {
                writer.WriteElementString("ContinuationToken", continuationToken);
            }

            if (nextToken is not null)
            {
                writer.WriteElementString("NextContinuationToken", nextToken);
            }

            if (startAfter is not null)
            {
                writer.WriteElementString("StartAfter", EncodeListingValue(startAfter, encodingType));
            }

            foreach (ObjectListEntry entry in page.Entries.Where(entry => !entry.IsCommonPrefix))
            {
                ObjectInfo item = entry.Object!;
                writer.WriteStartElement("Contents");
                writer.WriteElementString("Key", EncodeListingValue(item.Key, encodingType));
                writer.WriteElementString("LastModified", item.ModifiedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                writer.WriteElementString("ETag", QuoteEtag(item.BlobId));
                writer.WriteElementString("Size", item.Size.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("StorageClass", "STANDARD");
                writer.WriteEndElement();
            }

            foreach (ObjectListEntry entry in page.Entries.Where(entry => entry.IsCommonPrefix))
            {
                writer.WriteStartElement("CommonPrefixes");
                writer.WriteElementString("Prefix", EncodeListingValue(entry.Key, encodingType));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        await context.Response.WriteAsync(body.ToString(), context.RequestAborted).ConfigureAwait(false);
    }

    private static void ApplyObjectHeaders(HttpResponse response, ObjectInfo objectInfo, long? contentLength = null)
    {
        response.ContentLength = contentLength ?? objectInfo.Size;
        response.ContentType = objectInfo.ContentType ?? "application/octet-stream";
        response.Headers.ETag = QuoteEtag(objectInfo.BlobId);
        response.Headers.LastModified = objectInfo.ModifiedAt.ToString("R", CultureInfo.InvariantCulture);
        foreach ((string name, string value) in objectInfo.Metadata)
        {
            response.Headers["x-amz-meta-" + name] = value;
        }
    }

    private static void Authorize(S3Credential credential, string bucket, S3Operation operation)
    {
        if (!credential.AllowedBuckets.Contains(bucket) || !credential.AllowedOperations.HasFlag(operation))
        {
            throw new S3ProtocolException(403, "AccessDenied", "The credential is not authorized for this bucket and operation.");
        }
    }

    private static S3Operation GetOperation(HttpRequest request, string? key)
    {
        if (key is null)
        {
            if (request.Method == "GET" && string.Equals(request.Query["list-type"], "2", StringComparison.Ordinal))
            {
                return S3Operation.ListBucket;
            }

            throw new S3ProtocolException(501, "NotImplemented", "This bucket operation is not implemented.");
        }

        return request.Method switch
        {
            "PUT" => S3Operation.PutObject,
            "GET" => S3Operation.GetObject,
            "HEAD" => S3Operation.HeadObject,
            "DELETE" => S3Operation.DeleteObject,
            _ => throw new S3ProtocolException(501, "NotImplemented", "The requested S3 operation is not implemented.")
        };
    }

    private static (string Bucket, string? Key) ParseResource(HttpRequest request, string endpointPrefix)
    {
        string path = request.Path.Value ?? string.Empty;
        if (!path.StartsWith(endpointPrefix + "/", StringComparison.Ordinal))
        {
            throw new S3ProtocolException(400, "InvalidURI", "The request URI is invalid for this endpoint.");
        }

        string resource = path[(endpointPrefix.Length + 1)..];
        int separator = resource.IndexOf('/');
        if (separator == 0 || resource.Length == 0)
        {
            throw new S3ProtocolException(501, "NotImplemented", "Only path-style object operations are supported.");
        }

        return separator < 0
            ? (resource, null)
            : (resource[..separator], separator == resource.Length - 1 ? null : resource[(separator + 1)..]);
    }

    private void ValidateRequestLimits(HttpRequest request, string endpointPrefix)
    {
        string rawTarget = request.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget
            ?? request.Path + request.QueryString;
        int queryIndex = rawTarget.IndexOf('?');
        if (queryIndex >= 0
            && Encoding.UTF8.GetByteCount(rawTarget[(queryIndex + 1)..]) > _options.MaxQueryStringBytes)
        {
            throw new S3ProtocolException(400, "InvalidArgument", "The query string exceeds the configured limit.");
        }

        if (request.Headers.Count > _options.MaxHeaderCount)
        {
            throw new S3ProtocolException(400, "InvalidRequest", "The request contains too many headers.");
        }

        int headerBytes = 0;
        foreach ((string name, Microsoft.Extensions.Primitives.StringValues values) in request.Headers)
        {
            headerBytes = checked(headerBytes + Encoding.UTF8.GetByteCount(name));
            foreach (string? value in values)
            {
                headerBytes = checked(headerBytes + Encoding.UTF8.GetByteCount(value ?? string.Empty));
            }

            if (headerBytes > _options.MaxHeaderBytes)
            {
                throw new S3ProtocolException(400, "InvalidRequest", "The request headers exceed the configured limit.");
            }
        }

        string path = request.Path.Value ?? string.Empty;
        if (path.StartsWith(endpointPrefix + "/", StringComparison.Ordinal))
        {
            string resource = path[(endpointPrefix.Length + 1)..];
            int separator = resource.IndexOf('/');
            if (separator >= 0)
            {
                string key = resource[(separator + 1)..];
                if (Encoding.UTF8.GetByteCount(key) > _options.MaxKeyLengthBytes
                    || key.Any(static character => character == '\0' || char.IsControl(character)))
                {
                    throw new S3ProtocolException(400, "InvalidArgument", "The object key is invalid or exceeds the configured limit.");
                }
            }
        }
    }

    private static void ValidateQuery(HttpRequest request, S3Operation operation)
    {
        HashSet<string> allowed = operation == S3Operation.ListBucket
            ? new HashSet<string>(
                ["list-type", "prefix", "delimiter", "max-keys", "continuation-token", "start-after", "encoding-type", "x-id"],
                StringComparer.Ordinal)
            : new HashSet<string>(["x-id"], StringComparer.Ordinal);
        if (request.Query.Keys.Any(key => !allowed.Contains(key)))
        {
            throw new S3ProtocolException(501, "NotImplemented", "This S3 query operation is not implemented.");
        }
    }

    private static void ValidateFeatureHeaders(HttpRequest request, S3Operation operation)
    {
        string[] unsupported =
        [
            "If-Match",
            "If-None-Match",
            "If-Modified-Since",
            "If-Unmodified-Since",
            "If-Range",
            "x-amz-copy-source",
            "x-amz-acl",
            "x-amz-tagging"
        ];
        if (unsupported.Any(request.Headers.ContainsKey))
        {
            throw new S3ProtocolException(501, "NotImplemented", "A request header requires an S3 feature not implemented by this slice.");
        }
        if (request.Headers.ContainsKey("Range") && operation != S3Operation.GetObject)
            throw new S3ProtocolException(501, "NotImplemented", "Range is supported only for GetObject.");
    }

    private static ObjectByteRange ParseRange(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            throw new S3ProtocolException(400, "InvalidArgument", "Only the bytes range unit is supported.");
        string specification = value["bytes=".Length..].Trim();
        if (specification.Contains(','))
            throw new S3ProtocolException(501, "NotImplemented", "Multiple byte ranges are not supported.");
        string[] parts = specification.Split('-');
        if (parts.Length != 2 || (parts[0].Length == 0 && parts[1].Length == 0))
            throw new S3ProtocolException(400, "InvalidArgument", "The Range header is malformed.");

        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long suffix))
                throw new S3ProtocolException(400, "InvalidArgument", "The Range header is malformed.");
            return ObjectByteRange.FromSuffix(suffix);
        }

        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start))
            throw new S3ProtocolException(400, "InvalidArgument", "The Range header is malformed.");
        if (parts[1].Length == 0)
            return ObjectByteRange.FromStart(start);
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long end))
            throw new S3ProtocolException(400, "InvalidArgument", "The Range header is malformed.");
        if (end < start)
            throw new S3ProtocolException(400, "InvalidArgument", "The Range header is malformed.");
        return ObjectByteRange.FromStartAndEnd(start, end);
    }

    private static string QuoteEtag(string blobId) => "\"" + blobId + "-1\"";

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string EncodeListingValue(string value, string? encodingType) =>
        encodingType == "url" ? Uri.EscapeDataString(value) : value;

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message, string requestId, long? unsatisfiedLength = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/xml";
        context.Response.Headers["x-amz-request-id"] = requestId;
        context.Response.Headers["x-amz-id-2"] = requestId;
        if (unsatisfiedLength is not null)
            context.Response.Headers.ContentRange = $"bytes */{unsatisfiedLength.Value}";

        StringBuilder body = new();
        using (XmlWriter writer = XmlWriter.Create(body, new XmlWriterSettings { OmitXmlDeclaration = true, Async = false }))
        {
            writer.WriteStartElement("Error");
            writer.WriteElementString("Code", code);
            writer.WriteElementString("Message", message);
            writer.WriteElementString("RequestId", requestId);
            writer.WriteElementString("HostId", requestId);
            writer.WriteEndElement();
        }

        await context.Response.WriteAsync(body.ToString()).ConfigureAwait(false);
    }
}
