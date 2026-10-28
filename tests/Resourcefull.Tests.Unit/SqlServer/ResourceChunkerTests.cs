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
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(stream);

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
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(stream);

        // Assert
        Assert.Equal([1, 2, 3], chunks.Select(c => c.Index));
    }

    [Fact]
    public async Task ChunksAsync_ShouldReturnEmpty_WhenStreamIsEmpty()
    {
        // Arrange
        Stream stream = new MemoryStream();

        // Act
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(stream);

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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResourceChunker.ChunksAsync(stream, cts.Token));
    }
}
