namespace Resourcefull;

/// <summary>
/// Writes to <paramref name="destination"/> only asynchronously, so a zip can be written to a stream that refuses
/// synchronous writes, such as an ASP.NET Core response body. Closing a zip entry writes its last few bytes
/// synchronously; those are held here and passed on before the next asynchronous write, or by <see cref="FlushAsync"/>.
/// </summary>
internal sealed class AsyncWriteStream(Stream destination) : Stream
{
    private readonly MemoryStream _held = new();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => _held.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => _held.Write(buffer);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await PassOnHeldAsync(cancellationToken);
        await destination.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    // Held until FlushAsync, which passes it on.
    public override void Flush() { }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await PassOnHeldAsync(cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private async Task PassOnHeldAsync(CancellationToken cancellationToken)
    {
        if (_held.Length == 0) return;
        await destination.WriteAsync(_held.GetBuffer().AsMemory(0, (int)_held.Length), cancellationToken);
        _held.SetLength(0);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
