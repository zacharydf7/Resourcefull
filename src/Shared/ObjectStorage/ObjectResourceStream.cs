using Amazon.S3.Model;

namespace Resourcefull.S3;

/// <summary>
/// A version's content as the store sends it: read-only and forward-only, with the content's full length as
/// <see cref="Length"/> and, for a range, a <see cref="Position"/> that starts where the range does.
/// </summary>
internal sealed class S3ResourceStream(GetObjectResponse? response, long length, long position) : Stream
{
    private readonly Stream _body = response?.ResponseStream ?? Null;
    private long _position = position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = _body.Read(buffer);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int read = await _body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) response?.Dispose();
        base.Dispose(disposing);
    }
}
