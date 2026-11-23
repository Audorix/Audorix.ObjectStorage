using System.Security.Cryptography;

namespace Audorix.ObjectStorage.S3;

internal sealed class HashValidatingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _expectedSha256;
    private readonly byte[]? _expectedMd5;
    private readonly IncrementalHash _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash? _md5;
    private bool _validated;

    public HashValidatingReadStream(Stream inner, string expectedSha256, string? contentMd5)
    {
        _inner = inner;
        _expectedSha256 = Convert.FromHexString(expectedSha256);
        if (!string.IsNullOrEmpty(contentMd5))
        {
            try
            {
                _expectedMd5 = Convert.FromBase64String(contentMd5);
                if (_expectedMd5.Length != 16)
                {
                    throw new FormatException();
                }
            }
            catch (FormatException)
            {
                throw new S3ProtocolException(400, "InvalidDigest", "Content-MD5 is not a valid MD5 digest.");
            }

            _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        Process(buffer.AsSpan(offset, read), read == 0);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Process(buffer.Span[..read], read == 0);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Process(buffer.AsSpan(offset, read), read == 0);
        return read;
    }

    private void Process(ReadOnlySpan<byte> bytes, bool endOfStream)
    {
        if (_validated)
        {
            return;
        }

        if (!bytes.IsEmpty)
        {
            _sha256.AppendData(bytes);
            _md5?.AppendData(bytes);
        }

        if (!endOfStream)
        {
            return;
        }

        _validated = true;
        byte[] actualSha256 = _sha256.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualSha256, _expectedSha256))
        {
            throw new S3ProtocolException(400, "XAmzContentSHA256Mismatch", "The provided x-amz-content-sha256 does not match the request body.");
        }

        if (_expectedMd5 is not null)
        {
            byte[] actualMd5 = _md5!.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(actualMd5, _expectedMd5))
            {
                throw new S3ProtocolException(400, "BadDigest", "The Content-MD5 value does not match the request body.");
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sha256.Dispose();
            _md5?.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
