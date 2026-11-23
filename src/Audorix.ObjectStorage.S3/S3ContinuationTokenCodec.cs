using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Audorix.ObjectStorage.S3;

internal sealed class S3ContinuationTokenCodec
{
    private readonly byte[] _signingKey;
    private readonly TimeSpan _lifetime;
    private readonly int _maxTokenLength;

    public S3ContinuationTokenCodec(IOptions<S3EndpointOptions> options)
    {
        _signingKey = Encoding.UTF8.GetBytes(options.Value.ContinuationTokenSigningKey);
        _lifetime = options.Value.ContinuationTokenLifetime;
        _maxTokenLength = options.Value.MaxContinuationTokenLength;
    }

    public string Encode(
        string bucket,
        string prefix,
        string? delimiter,
        string? encodingType,
        string afterEntry,
        string accessKeyId)
    {
        TokenPayload payload = new(2, bucket, prefix, delimiter, encodingType, afterEntry, accessKeyId, DateTimeOffset.UtcNow.Add(_lifetime));
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] signature = HMACSHA256.HashData(_signingKey, payloadBytes);
        return Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    public string Decode(
        string token,
        string bucket,
        string prefix,
        string? delimiter,
        string? encodingType,
        string accessKeyId)
    {
        try
        {
            if (token.Length > _maxTokenLength)
            {
                throw InvalidToken();
            }

            string[] parts = token.Split('.', 2);
            if (parts.Length != 2)
            {
                throw InvalidToken();
            }

            byte[] payloadBytes = Base64UrlDecode(parts[0]);
            byte[] suppliedSignature = Base64UrlDecode(parts[1]);
            byte[] expectedSignature = HMACSHA256.HashData(_signingKey, payloadBytes);
            if (suppliedSignature.Length != expectedSignature.Length
                || !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
            {
                throw InvalidToken();
            }

            TokenPayload? payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
            if (payload is null
                || payload.Version != 2
                || !string.Equals(payload.Bucket, bucket, StringComparison.Ordinal)
                || !string.Equals(payload.Prefix, prefix, StringComparison.Ordinal)
                || !string.Equals(payload.Delimiter, delimiter, StringComparison.Ordinal)
                || !string.Equals(payload.EncodingType, encodingType, StringComparison.Ordinal)
                || !string.Equals(payload.AccessKeyId, accessKeyId, StringComparison.Ordinal)
                || payload.ExpiresAt <= DateTimeOffset.UtcNow
                || string.IsNullOrEmpty(payload.AfterEntry))
            {
                throw InvalidToken();
            }

            return payload.AfterEntry;
        }
        catch (S3ProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw InvalidToken();
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    private static S3ProtocolException InvalidToken() =>
        new(400, "InvalidArgument", "The continuation token is malformed or does not match this listing request.");

    private sealed record TokenPayload(
        int Version,
        string Bucket,
        string Prefix,
        string? Delimiter,
        string? EncodingType,
        string AfterEntry,
        string AccessKeyId,
        DateTimeOffset ExpiresAt);
}
