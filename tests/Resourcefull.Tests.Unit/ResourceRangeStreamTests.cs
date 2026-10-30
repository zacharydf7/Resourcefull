namespace Resourcefull.Tests.Unit;

public class ResourceRangeStreamTests
{
    // Ten bytes, 0 to 9, positioned at 2 by the provider, of which the range is the next five.
    private static ResourceRangeStream NewRange(out TrackingStream content)
    {
        content = new TrackingStream([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]) { Position = 2 };
        return new ResourceRangeStream(content, 5);
    }

    [Fact]
    public void Read_ShouldStopAtTheRangesEnd_WhenReadSynchronouslyIntoAnArray()
    {
        // Arrange
        using ResourceRangeStream range = NewRange(out _);
        var buffer = new byte[4];
        var read = new List<byte>();

        // Act
        int count;
        while ((count = range.Read(buffer, 0, buffer.Length)) > 0) read.AddRange(buffer[..count]);

        // Assert
        Assert.Equal([2, 3, 4, 5, 6], read);
        Assert.Equal(5, range.Position);
    }

    [Fact]
    public void Read_ShouldStopAtTheRangesEnd_WhenReadSynchronouslyIntoASpan()
    {
        // Arrange
        using ResourceRangeStream range = NewRange(out _);
        Span<byte> buffer = stackalloc byte[16];

        // Act
        int first = range.Read(buffer);
        int second = range.Read(buffer);

        // Assert
        Assert.Equal([2, 3, 4, 5, 6], buffer[..first].ToArray());
        Assert.Equal(0, second);
    }

    [Fact]
    public void Dispose_ShouldDisposeTheContent_WhenCalled()
    {
        // Arrange
        ResourceRangeStream range = NewRange(out TrackingStream content);

        // Act
        range.Dispose();

        // Assert
        Assert.True(content.Disposed);
        Assert.False(content.DisposedAsync);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeTheContentAsynchronously_WhenCalled()
    {
        // Arrange
        ResourceRangeStream range = NewRange(out TrackingStream content);

        // Act
        await range.DisposeAsync();

        // Assert: a provider's stream, such as SQL Server's, can block when disposed synchronously.
        Assert.True(content.DisposedAsync);
        Assert.False(content.Disposed);
    }
}
