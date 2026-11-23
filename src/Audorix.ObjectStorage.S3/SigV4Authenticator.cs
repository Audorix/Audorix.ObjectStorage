using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Audorix.ObjectStorage.S3;

internal sealed record S3AuthenticatedRequest(S3Credential Credential, string PayloadHash);

internal sealed class SigV4Authenticator
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private readonly S3EndpointOptions _options;

    public SigV4Authenticator(IOptions<S3EndpointOptions> options)
    {
        _options = options.Value;
    }

    public S3AuthenticatedRequest Authenticate(HttpRequest request, bool hasPayload)
    {
        string authorization = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            throw Error(403, "AccessDenied", "AWS Signature Version 4 Authorization header is required.");
        }

        ParsedAuthorization parsed = ParseAuthorization(authorization);
        string? unsignedAmazonHeader = request.Headers.Keys.FirstOrDefault(name =>
            name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("x-amz-content-sha256", StringComparison.OrdinalIgnoreCase)
            && !parsed.SignedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase));
        if (unsignedAmazonHeader is not null)
        {
            throw Error(400, "AuthorizationHeaderMalformed", "Every x-amz-* request header must be included in SignedHeaders.");
        }

        S3Credential credential = _options.Credentials.FirstOrDefault(
            candidate => string.Equals(candidate.AccessKeyId, parsed.AccessKeyId, StringComparison.Ordinal))
            ?? throw Error(403, "InvalidAccessKeyId", "The AWS access key ID does not exist.");

        string timestamp = request.Headers["x-amz-date"].ToString();
        if (!DateTimeOffset.TryParseExact(
                timestamp,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset signedAt))
        {
            throw Error(400, "AuthorizationHeaderMalformed", "x-amz-date is missing or malformed.");
        }

        if (DateTimeOffset.UtcNow.Subtract(signedAt).Duration() > _options.AllowedClockSkew)
        {
            throw Error(403, "RequestTimeTooSkewed", "The difference between the request time and server time is too large.");
        }

        string date = timestamp[..8];
        if (!string.Equals(parsed.Date, date, StringComparison.Ordinal)
            || !string.Equals(parsed.Region, _options.Region, StringComparison.Ordinal)
            || !string.Equals(parsed.Service, "s3", StringComparison.Ordinal)
            || !string.Equals(parsed.Terminator, "aws4_request", StringComparison.Ordinal))
        {
            throw Error(400, "AuthorizationHeaderMalformed", "The credential scope is invalid for this endpoint.");
        }

        string payloadHash = request.Headers["x-amz-content-sha256"].ToString();
        if (string.IsNullOrEmpty(payloadHash))
        {
            throw Error(400, "InvalidRequest", "x-amz-content-sha256 is required.");
        }

        if (payloadHash.StartsWith("STREAMING-", StringComparison.Ordinal)
            || request.Headers.ContentEncoding.Any(value => value?.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase) == true)
            || request.Headers.ContainsKey("x-amz-trailer"))
        {
            throw Error(501, "NotImplemented", "aws-chunked payloads and checksum trailers are not supported.");
        }

        if (hasPayload)
        {
            if (!IsSha256(payloadHash))
            {
                throw Error(400, "InvalidRequest", "PUT requires a signed SHA-256 payload hash.");
            }

            if (request.Headers.Keys.Any(static name => name.StartsWith("x-amz-checksum-", StringComparison.OrdinalIgnoreCase))
                || request.Headers.ContainsKey("x-amz-sdk-checksum-algorithm"))
            {
                throw Error(501, "NotImplemented", "Additional S3 checksum headers are not supported in this slice.");
            }
        }
        else if (!IsSha256(payloadHash) && !string.Equals(payloadHash, "UNSIGNED-PAYLOAD", StringComparison.Ordinal))
        {
            throw Error(400, "InvalidRequest", "The payload hash mode is not supported.");
        }

        string canonicalHeaders = BuildCanonicalHeaders(request, parsed.SignedHeaders);
        string signedHeaders = string.Join(';', parsed.SignedHeaders);
        string canonicalRequest = string.Join('\n',
            request.Method.ToUpperInvariant(),
            CanonicalizePath(GetRawPath(request)),
            CanonicalizeQuery(GetRawQuery(request)),
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        string scope = $"{parsed.Date}/{parsed.Region}/{parsed.Service}/{parsed.Terminator}";
        string stringToSign = string.Join('\n', Algorithm, timestamp, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));
        byte[] signingKey = DeriveSigningKey(credential.SecretAccessKey, parsed.Date, parsed.Region, parsed.Service);
        byte[] expected = Hmac(signingKey, stringToSign);

        if (!TryDecodeHex(parsed.Signature, out byte[]? supplied)
            || supplied.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            throw Error(403, "SignatureDoesNotMatch", "The request signature does not match the signature calculated by the service.");
        }

        return new S3AuthenticatedRequest(credential, payloadHash.ToLowerInvariant());
    }

    private static ParsedAuthorization ParseAuthorization(string authorization)
    {
        if (!authorization.StartsWith(Algorithm + " ", StringComparison.Ordinal))
        {
            throw Error(400, "AuthorizationHeaderMalformed", "Only AWS Signature Version 4 is supported.");
        }

        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string part in authorization[(Algorithm.Length + 1)..]
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1]))
            {
                throw Error(400, "AuthorizationHeaderMalformed", "Authorization fields are malformed or duplicated.");
            }
        }

        if (!fields.TryGetValue("Credential", out string? credentialValue)
            || !fields.TryGetValue("SignedHeaders", out string? signedHeadersValue)
            || !fields.TryGetValue("Signature", out string? signature))
        {
            throw Error(400, "AuthorizationHeaderMalformed", "Authorization is missing required fields.");
        }

        string[] scope = credentialValue.Split('/');
        string[] signedHeaders = signedHeadersValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (scope.Length != 5
            || signedHeaders.Length == 0
            || !signedHeaders.Contains("host", StringComparer.Ordinal)
            || !signedHeaders.SequenceEqual(signedHeaders.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || signedHeaders.Any(name => name != name.ToLowerInvariant())
            || signedHeaders.Distinct(StringComparer.Ordinal).Count() != signedHeaders.Length)
        {
            throw Error(400, "AuthorizationHeaderMalformed", "Credential scope or signed headers are malformed.");
        }

        return new ParsedAuthorization(scope[0], scope[1], scope[2], scope[3], scope[4], signedHeaders, signature);
    }

    private static string BuildCanonicalHeaders(HttpRequest request, IReadOnlyList<string> signedHeaders)
    {
        StringBuilder builder = new();
        foreach (string name in signedHeaders)
        {
            string value;
            if (name == "host")
            {
                value = request.Host.Value ?? string.Empty;
                if (value.Length == 0)
                {
                    throw Error(400, "AuthorizationHeaderMalformed", "The Host header is required.");
                }
            }
            else if (!request.Headers.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues values))
            {
                throw Error(400, "AuthorizationHeaderMalformed", $"Signed header '{name}' is missing.");
            }
            else
            {
                value = string.Join(',', values.ToArray()) ?? string.Empty;
            }

            builder.Append(name).Append(':').Append(CollapseWhitespace(value)).Append('\n');
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string CanonicalizePath(string rawPath)
    {
        if (string.IsNullOrEmpty(rawPath))
        {
            return "/";
        }

        return string.Join('/', rawPath.Split('/').Select(segment => AwsEncode(Uri.UnescapeDataString(segment))));
    }

    private static string CanonicalizeQuery(string rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
        {
            return string.Empty;
        }

        return string.Join('&', rawQuery.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Select(parts => (Name: AwsEncode(Uri.UnescapeDataString(parts[0])), Value: AwsEncode(Uri.UnescapeDataString(parts.Length == 2 ? parts[1] : string.Empty))))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .Select(item => item.Name + "=" + item.Value));
    }

    private static string AwsEncode(string value)
    {
        StringBuilder builder = new();
        foreach (byte valueByte in Encoding.UTF8.GetBytes(value))
        {
            char character = (char)valueByte;
            if ((character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character is '-' or '_' or '.' or '~')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('%').Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static string GetRawPath(HttpRequest request)
    {
        string target = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget
            ?? request.Path.Value
            ?? "/";
        int queryIndex = target.IndexOf('?');
        return queryIndex < 0 ? target : target[..queryIndex];
    }

    private static string GetRawQuery(HttpRequest request)
    {
        string target = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? string.Empty;
        int queryIndex = target.IndexOf('?');
        return queryIndex < 0 ? string.Empty : target[queryIndex..];
    }

    private static byte[] DeriveSigningKey(string secret, string date, string region, string service)
    {
        byte[] dateKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), date);
        byte[] regionKey = Hmac(dateKey, region);
        byte[] serviceKey = Hmac(regionKey, service);
        return Hmac(serviceKey, "aws4_request");
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static S3ProtocolException Error(int statusCode, string code, string message) => new(statusCode, code, message);

    private sealed record ParsedAuthorization(
        string AccessKeyId,
        string Date,
        string Region,
        string Service,
        string Terminator,
        string[] SignedHeaders,
        string Signature);
}
