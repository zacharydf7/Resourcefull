namespace Resourcefull.InMemory;

/// <summary>
/// Stored content as a read-only, forward-only stream: the same capabilities as the SQL Server provider's, so code
/// that seeks a download, or treats it as a <see cref="MemoryStream"/>, fails in tests rather than in production. For
/// a range, it starts at <paramref name="offset"/>.
/// </summary>
internal sealed class InMemoryResourceStream(byte[] content, long offset) : Stream
{
    private readonly MemoryStream _content = new(content, writable: false) { Position = Math.Min(offset, content.Length) };

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => content.Length;

    public override long Position
    {
        get => _content.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _content.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _content.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _content.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _content.ReadAsync(buffer, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _content.Dispose();
        base.Dispose(disposing);
    }
}
