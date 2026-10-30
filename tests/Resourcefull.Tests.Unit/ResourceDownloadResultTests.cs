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

    /// <summary>Records which dispose path ran.</summary>
    private sealed class TrackingStream : MemoryStream
    {
        public bool Disposed { get; private set; }

        public bool DisposedAsync { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            DisposedAsync = true;
            return ValueTask.CompletedTask;
        }
    }
}
