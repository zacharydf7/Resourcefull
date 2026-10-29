using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.Utilities;

namespace Resourcefull.Tests.Unit.SqlServer;
public class ResourceChunkerTests
{

    [Fact]
    public async Task ChunksAsync_ShouldReturnResourceChunks_WhenResourceIsSuccessfullyChunked()
    {
        // Arrange
        var chunkSize = 65535;
        var numberOfChunks = 3;
        var bytes = new byte[chunkSize * numberOfChunks];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)Math.Floor((double)i / chunkSize);
        }
        Stream stream = new MemoryStream(bytes);

        // Act
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(stream, reuseAfter: 16).ToListAsync();

        // Assert
        Assert.Collection(chunks,
            firstChunk =>
            {
                Assert.All(firstChunk.Data!, dataByte => Assert.Equal(0, dataByte));
            },
            secondChunk =>
            {
                Assert.All(secondChunk.Data!, dataByte => Assert.Equal(1, dataByte));
            },
            thirdChunk =>
            {
                Assert.All(thirdChunk.Data!, dataByte => Assert.Equal(2, dataByte));
            });
    }

    [Fact]
    public async Task ChunksAsync_ShouldNumberChunksSequentiallyFromOne_WhenStreamSpansMultipleChunks()
    {
        // Arrange
        var bytes = new byte[65535 * 2 + 10];
        Stream stream = new MemoryStream(bytes);

        // Act
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(stream, reuseAfter: 16).ToListAsync();

        // Assert
        Assert.Equal([1, 2, 3], chunks.Select(c => c.Index));
    }

    [Fact]
    public async Task ChunksAsync_ShouldReturnEmpty_WhenStreamIsEmpty()
    {
        // Arrange
        Stream stream = new MemoryStream();

        // Act
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(stream, reuseAfter: 16).ToListAsync();

        // Assert
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task ChunksAsync_ShouldThrowOperationCanceled_WhenTokenIsCancelledMidStream()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var stream = new CancelAfterReadsStream(new byte[65535 * 3], cts, readsBeforeCancel: 1);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResourceChunker.ChunksAsync(stream, reuseAfter: 16, cts.Token).ToListAsync().AsTask());
    }

    [Fact]
    public async Task ChunksAsync_ShouldFillEveryChunkButTheLast_WhenTheStreamReturnsShortReads()
    {
        // Arrange
        byte[] bytes = new byte[65535 * 2 + 10];
        new Random(1).NextBytes(bytes);
        var stream = new ShortReadStream(bytes, maxRead: 1000);

        // Act
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(stream, reuseAfter: 16).ToListAsync();

        // Assert
        Assert.Equal([65535, 65535, 10], chunks.Select(c => c.Data.Length));
        Assert.Equal(bytes, chunks.SelectMany(c => c.Data));
    }

    [Fact]
    public async Task ChunksAsync_ShouldShareFullChunksBuffersInTurnAndGiveTheLastItsOwn_WhenGivenReuseAfter()
    {
        // Arrange: five chunks, each filled with its own number, read two at a time as the repository stores them.
        byte[] bytes = new byte[65535 * 4 + 10];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i / 65535);
        var chunks = new List<ResourceChunkModel>();
        var contentAsRead = new List<byte[]>();

        // Act
        await foreach (ResourceChunkModel chunk in ResourceChunker.ChunksAsync(new MemoryStream(bytes), reuseAfter: 2))
        {
            chunks.Add(chunk);
            contentAsRead.Add(chunk.Data.ToArray());
        }

        // Assert
        Assert.Equal(bytes, contentAsRead.SelectMany(data => data));
        Assert.Same(chunks[0].Data, chunks[2].Data);
        Assert.Same(chunks[1].Data, chunks[3].Data);
        Assert.NotSame(chunks[0].Data, chunks[1].Data);
        Assert.Equal(10, chunks[4].Data.Length);
    }

    /// <summary>Returns at most <paramref name="maxRead"/> bytes per read, as a network stream may.</summary>
    private sealed class ShortReadStream(byte[] content, int maxRead) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
    }
}
