namespace Audorix.ObjectStorage;

internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;

    public BoundedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int permitted = (int)Math.Min(count, _length - _position);
        if (permitted <= 0) return 0;
        int read = _inner.Read(buffer, offset, permitted);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int permitted = (int)Math.Min(buffer.Length, _length - _position);
        if (permitted <= 0) return 0;
        int read = await _inner.ReadAsync(buffer[..permitted], cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > _length) throw new IOException("Cannot seek outside the selected byte range.");
        _inner.Seek(target - _position, SeekOrigin.Current);
        _position = target;
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await _inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); }
}
