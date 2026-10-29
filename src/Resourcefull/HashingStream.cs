using System.Security.Cryptography;

namespace Resourcefull;

/// <summary>
/// Passes reads through from another stream while counting and hashing every byte read. Providers read the content
/// they store through one and return <see cref="Complete(long?, DateTimeOffset)"/>, with the time the version became
/// current, from <see cref="IResourceRepository.AddResourceAsync"/>.
/// Disposing it does not dispose the source stream.
/// </summary>
public sealed class HashingStream(Stream source) : Stream
{
    private const string HashPrefix = "sha256:";

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesRead;

    /// <inheritdoc />
    public override bool CanRead => true;
    /// <inheritdoc />
    public override bool CanSeek => false;
    /// <inheritdoc />
    public override bool CanWrite => false;
    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Gets what was read, failing when it disagrees with the length the caller declared. Call it as the write makes
    /// the version current, but before it commits, so a wrong declared length stores nothing.
    /// </summary>
    /// <param name="declaredLength">The length the caller declared, if any.</param>
    /// <param name="lastModified">When the version became current, by the repository's clock.</param>
    /// <exception cref="ResourceContentLengthMismatchException">The declared length differs from the number of bytes read.</exception>
    public WrittenContent Complete(long? declaredLength, DateTimeOffset lastModified)
    {
        if (declaredLength is not null && declaredLength != _bytesRead)
        {
            throw new ResourceContentLengthMismatchException(
                $"The content length was declared as {declaredLength} bytes, but the content was {_bytesRead} bytes.");
        }

        return new WrittenContent(_bytesRead, HashPrefix + Convert.ToHexStringLower(_hash.GetCurrentHash()), lastModified);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        int bytesRead = source.Read(buffer);
        Append(buffer[..bytesRead]);
        return bytesRead;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = await source.ReadAsync(buffer, cancellationToken);
        Append(buffer.Span[..bytesRead]);
        return bytesRead;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
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
