namespace Resourcefull;

/// <summary>
/// A range of a version's content, as a range download returns it: the next <paramref name="length"/> bytes of
/// <paramref name="content"/>, which the provider positioned at the range's start. Its length and position are the
/// range's, and it stops at the range's end even when the provider's stream goes on.
/// </summary>
internal sealed class ResourceRangeStream(Stream content, long length) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Counted(content.Read(buffer, offset, Limit(count)));

    public override int Read(Span<byte> buffer) => Counted(content.Read(buffer[..Limit(buffer.Length)]));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await content.ReadAsync(buffer[..Limit(buffer.Length)], cancellationToken));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Counted(await content.ReadAsync(buffer.AsMemory(offset, Limit(count)), cancellationToken));

    private int Limit(int count) => (int)Math.Min(count, length - _read);

    private int Counted(int read)
    {
        _read += read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) content.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => content.DisposeAsync();
}
