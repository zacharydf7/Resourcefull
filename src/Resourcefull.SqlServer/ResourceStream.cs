namespace Resourcefull.SqlServer;

/// <summary>
/// A version's content, fetched a page of chunks at a time as the caller reads, so only one page is held at once.
/// </summary>
/// <param name="nextPage">Fetches the next chunks' data in index order, with the token of the read that needs them;
/// empty once the content is exhausted. A page's data need only stay valid until the next page is fetched.</param>
/// <param name="length">The content's full length; a read that ends before it throws, as the content was cut short.</param>
/// <param name="owner">What the pages are read through, such as the database context; disposed with the stream.</param>
internal class ResourceStream(Func<CancellationToken, Task<IReadOnlyList<ReadOnlyMemory<byte>>>> nextPage, long length,
    IAsyncDisposable? owner = null) : Stream
{
    private IReadOnlyList<ReadOnlyMemory<byte>> _page = [];
    private int _pageIndex = 0;
    private bool _exhausted = false;
    private ReadOnlyMemory<byte> _currentChunkData = ReadOnlyMemory<byte>.Empty;
    private int _currentChunkPosition = 0;
    private long _position = 0;

    // Awaits don't resume on the caller's context: Read blocks on ReadAsync, and would deadlock a single-threaded one.
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (_currentChunkPosition >= _currentChunkData.Length)
        {
            if (_pageIndex < _page.Count)
            {
                _currentChunkData = _page[_pageIndex++];
                _currentChunkPosition = 0;
            }
            else if (!_exhausted)
            {
                _page = await nextPage(cancellationToken).ConfigureAwait(false);
                _pageIndex = 0;
                _exhausted = _page.Count == 0;
            }
            else
            {
                if (_position < length)
                    throw new IOException($"The content ended after {_position} of its {length} bytes; it may have been deleted while it was read.");
                return 0;
            }
        }

        var bytesToCopy = Math.Min(count, _currentChunkData.Length - _currentChunkPosition);

        _currentChunkData.Span.Slice(_currentChunkPosition, bytesToCopy).CopyTo(buffer.AsSpan(offset, bytesToCopy));

        _currentChunkPosition += bytesToCopy;
        _position += bytesToCopy;

        return bytesToCopy;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            throw new NotSupportedException();
        }
    }

    public override void Flush()
    {
        // Nothing to do here as we're not writing to the stream
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await ReleaseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseAsync()
    {
        if (owner != null) await owner.DisposeAsync().ConfigureAwait(false);
        owner = null;
    }
}
