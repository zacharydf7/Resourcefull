using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer;

internal class ResourceStream(List<ResourceChunkModel> fileChunks) : Stream
{
    private Queue<ResourceChunkModel> _chunks = new(fileChunks.OrderBy(c => c.Index));
    private int _currentChunkIndex = 0;
    private byte[]? _currentChunkData;
    private int _currentChunkPosition = 0;

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_chunks.Count == 0 && (_currentChunkData == null || _currentChunkPosition >= _currentChunkData.Length)) return 0;

        if (_currentChunkData == null || _currentChunkPosition >= _currentChunkData.Length)
        {
            _currentChunkData = _chunks.Dequeue().Data;
            _currentChunkPosition = 0;
        }

        var bytesToCopy = Math.Min(count, _currentChunkData.Length - _currentChunkPosition);

        Array.Copy(_currentChunkData, _currentChunkPosition, buffer, offset, bytesToCopy);

        _currentChunkPosition += bytesToCopy;

        return await Task.FromResult(bytesToCopy);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _chunks?.Sum(chunk => chunk.Data.Length) ?? 0;

    public override long Position
    {
        get
        {
            if (_chunks == null) return 0;

            return _chunks.Take(_currentChunkIndex).Sum(chunk => chunk.Data.Length);
        }
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
        return ReadAsync(buffer, offset, count).Result;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotImplementedException();
    }

    public override void SetLength(long value)
    {
        throw new NotImplementedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotImplementedException();
    }
}