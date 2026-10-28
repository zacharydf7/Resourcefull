using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Models;
using System.Text;

namespace Resourcefull.Tests.Unit.SqlServer;

public class ResourceStreamTests
{
    private static ResourceChunkModel Chunk(int index, string data) => new() { Index = index, Data = Encoding.UTF8.GetBytes(data) };

    [Fact]
    public void Read_ShouldReassembleChunksInIndexOrder_WhenChunksArriveOutOfOrder()
    {
        // Arrange
        var stream = new ResourceStream([Chunk(3, "ccc"), Chunk(1, "aaa"), Chunk(2, "bbb")]);

        // Act
        var content = new StreamReader(stream).ReadToEnd();

        // Assert
        Assert.Equal("aaabbbccc", content);
    }

    [Fact]
    public async Task CopyToAsync_ShouldProduceFullContent_WhenContentSpansChunkBoundaries()
    {
        // Arrange
        var stream = new ResourceStream([Chunk(1, "Hello, "), Chunk(2, "World!")]);
        using var memoryStream = new MemoryStream();

        // Act
        await stream.CopyToAsync(memoryStream);

        // Assert
        Assert.Equal("Hello, World!", Encoding.UTF8.GetString(memoryStream.ToArray()));
    }

    [Fact]
    public void Read_ShouldReassembleContent_WhenReadOneByteAtATime()
    {
        // Arrange
        var stream = new ResourceStream([Chunk(1, "ab"), Chunk(2, "cd")]);
        var builder = new StringBuilder();
        var buffer = new byte[1];

        // Act
        while (stream.Read(buffer, 0, 1) > 0)
        {
            builder.Append((char)buffer[0]);
        }

        // Assert
        Assert.Equal("abcd", builder.ToString());
    }

    [Fact]
    public void Read_ShouldReturnZero_WhenThereAreNoChunks()
    {
        // Arrange
        var stream = new ResourceStream([]);

        // Act
        int read = stream.Read(new byte[10], 0, 10);

        // Assert
        Assert.Equal(0, read);
    }

    [Fact]
    public void ResourceStream_ShouldBeReadOnlyAndNonSeekable_WhenConstructed()
    {
        // Arrange & Act
        var stream = new ResourceStream([Chunk(1, "x")]);

        // Assert
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void ResourceStream_ShouldThrowNotSupported_WhenSeekWriteSetLengthOrPositionIsCalled()
    {
        // Arrange & Act
        var stream = new ResourceStream([Chunk(1, "x")]);

        // Assert
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
    }

    [Fact]
    public void ResourceStream_ShouldReportTheTotalLengthAndTheBytesReadSoFar_WhenReadPartWay()
    {
        // Arrange
        var stream = new ResourceStream([Chunk(1, "aaa"), Chunk(2, "bbb")]);

        // Act
        int read = stream.Read(new byte[4], 0, 4) + stream.Read(new byte[1], 0, 1);

        // Assert
        Assert.Equal(4, read);
        Assert.Equal(6, stream.Length);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task ReadAsync_ShouldThrowOperationCanceled_WhenTokenIsAlreadyCancelled()
    {
        // Arrange
        var stream = new ResourceStream([Chunk(1, "abc")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[10], 0, 10, cts.Token));
    }

    [Fact]
    public async Task CopyToAsync_ShouldStopPartWay_WhenTokenIsCancelledBetweenChunks()
    {
        // Arrange
        var stream = new ResourceStream([Chunk(1, "aaa"), Chunk(2, "bbb"), Chunk(3, "ccc")]);
        using var cts = new CancellationTokenSource();
        using var destination = new MemoryStream();
        var buffer = new byte[3];

        // Act
        int firstRead = await stream.ReadAsync(buffer, 0, 3, cts.Token);
        cts.Cancel();
        Task act() => stream.CopyToAsync(destination, cts.Token);

        // Assert
        Assert.Equal(3, firstRead);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Equal(0, destination.Length);
    }
}
