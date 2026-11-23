using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Audorix.ObjectStorage.Api;

internal sealed class ObjectStorageApiContinuationTokenCodec
{
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _lifetime;
    private readonly int _maxTokenLength;

    public ObjectStorageApiContinuationTokenCodec(IDataProtectionProvider provider, Microsoft.Extensions.Options.IOptions<ObjectStorageApiOptions> options)
    {
        _protector = provider.CreateProtector("Audorix.ObjectStorage.Api.ListingToken.v2").ToTimeLimitedDataProtector();
        _lifetime = options.Value.ContinuationTokenLifetime;
        _maxTokenLength = options.Value.MaxContinuationTokenLength;
    }

    public string Encode(
        string logicalScope,
        ObjectStorageApiGrant grant,
        string prefix,
        string? delimiter,
        int pageSize,
        string afterEntry)
    {
        TokenPayload payload = new(
            1,
            logicalScope,
            grant.BucketName,
            grant.KeyPrefix,
            prefix,
            delimiter,
            pageSize,
            afterEntry);
        return _protector.Protect(JsonSerializer.Serialize(payload), _lifetime);
    }

    public string Decode(
        string token,
        string logicalScope,
        ObjectStorageApiGrant grant,
        string prefix,
        string? delimiter,
        int pageSize)
    {
        try
        {
            if (token.Length > _maxTokenLength)
            {
                throw InvalidToken();
            }

            TokenPayload? payload = JsonSerializer.Deserialize<TokenPayload>(_protector.Unprotect(token));
            if (payload is null
                || payload.Version != 1
                || !string.Equals(payload.LogicalScope, logicalScope, StringComparison.Ordinal)
                || !string.Equals(payload.BucketName, grant.BucketName, StringComparison.Ordinal)
                || !string.Equals(payload.KeyPrefix, grant.KeyPrefix, StringComparison.Ordinal)
                || !string.Equals(payload.Prefix, prefix, StringComparison.Ordinal)
                || !string.Equals(payload.Delimiter, delimiter, StringComparison.Ordinal)
                || payload.PageSize != pageSize
                || string.IsNullOrEmpty(payload.AfterEntry))
            {
                throw InvalidToken();
            }

            return payload.AfterEntry;
        }
        catch (ObjectStorageApiRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            throw InvalidToken();
        }
    }

    private static ObjectStorageApiRequestException InvalidToken() =>
        new("The continuation token is invalid or does not match this listing request.");

    private sealed record TokenPayload(
        int Version,
        string LogicalScope,
        string BucketName,
        string KeyPrefix,
        string Prefix,
        string? Delimiter,
        int PageSize,
        string AfterEntry);
}

internal sealed class ObjectStorageApiRequestException(string message) : Exception(message);
