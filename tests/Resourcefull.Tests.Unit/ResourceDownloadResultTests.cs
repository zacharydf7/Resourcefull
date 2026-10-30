namespace Resourcefull.Tests.Unit;

public class ResourceDownloadResultTests
{
    private static ResourceDownloadResult NewDownload(Stream stream) =>
        new(new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "v1" }, stream);

    [Fact]
    public void Dispose_ShouldDisposeTheStream_WhenCalled()
    {
        // Arrange
        var stream = new TrackingStream();
        ResourceDownloadResult download = NewDownload(stream);

        // Act
        download.Dispose();

        // Assert
        Assert.True(stream.Disposed);
        Assert.False(stream.DisposedAsync);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeTheStreamAsynchronously_WhenUsedWithAwaitUsing()
    {
        // Arrange
        var stream = new TrackingStream();

        // Act
        await using (NewDownload(stream))
        {
        }

        // Assert: with SQL Server, the synchronous path blocks on releasing the database context.
        Assert.True(stream.DisposedAsync);
        Assert.False(stream.Disposed);
    }
}
