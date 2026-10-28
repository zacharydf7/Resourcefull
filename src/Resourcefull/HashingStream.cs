using System.Security.Cryptography;

namespace Resourcefull;

/// <summary>
/// Passes reads through from another stream while counting and hashing every byte read.
/// </summary>
internal class HashingStream(Stream source) : Stream
{
    private const string HashPrefix = "sha256:";

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesRead;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Gets what was read, failing when it disagrees with the length the caller declared.
    /// </summary>
    /// <exception cref="ResourceContentLengthMismatchException">The declared length differs from the number of bytes read.</exception>
    public WrittenContent Complete(long? declaredLength)
    {
        if (declaredLength is not null && declaredLength != _bytesRead)
        {
            throw new ResourceContentLengthMismatchException(
                $"The content length was declared as {declaredLength} bytes, but the content was {_bytesRead} bytes.");
        }

        return new WrittenContent(_bytesRead, HashPrefix + Convert.ToHexStringLower(_hash.GetCurrentHash()));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int bytesRead = source.Read(buffer);
        Append(buffer[..bytesRead]);
        return bytesRead;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = await source.ReadAsync(buffer, cancellationToken);
        Append(buffer.Span[..bytesRead]);
        return bytesRead;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();

        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        _bytesRead += bytes.Length;
    }
}
