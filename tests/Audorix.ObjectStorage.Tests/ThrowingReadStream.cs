namespace Audorix.ObjectStorage.Tests;

internal sealed class ThrowingReadStream : Stream
{
    private readonly byte[] _bytes;
    private readonly int _throwAfterBytes;
    private int _position;

    public ThrowingReadStream(byte[] bytes, int throwAfterBytes)
    {
        _bytes = bytes;
        _throwAfterBytes = throwAfterBytes;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _bytes.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _throwAfterBytes)
        {
            throw new IOException("Simulated upload failure.");
        }

        int available = Math.Min(count, Math.Min(_bytes.Length - _position, _throwAfterBytes - _position));
        Array.Copy(_bytes, _position, buffer, offset, available);
        _position += available;
        return available;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] temp = new byte[buffer.Length];
        int read = Read(temp, 0, temp.Length);
        temp.AsMemory(0, read).CopyTo(buffer);
        return ValueTask.FromResult(read);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
