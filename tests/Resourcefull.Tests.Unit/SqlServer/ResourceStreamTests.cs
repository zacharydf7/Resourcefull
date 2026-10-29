using Resourcefull.SqlServer;
using System.Text;

namespace Resourcefull.Tests.Unit.SqlServer;

public class ResourceStreamTests
{
    private static ResourceStream Stream(params string[] chunks)
    {
        byte[][] data = [.. chunks.Select(Encoding.UTF8.GetBytes)];
        return new ResourceStream(Pages(data), data.Sum(d => (long)d.Length));
    }

    /// <summary>One chunk per page, then no more.</summary>
    private static Func<CancellationToken, Task<IReadOnlyList<byte[]>>> Pages(params byte[][] chunks)
    {
        var remaining = new Queue<byte[]>(chunks);
        return _ => Task.FromResult<IReadOnlyList<byte[]>>(remaining.Count > 0 ? [remaining.Dequeue()] : []);
    }

    [Fact]
    public async Task CopyToAsync_ShouldProduceFullContent_WhenContentSpansChunkBoundaries()
    {
        // Arrange
        var stream = Stream("Hello, ", "World!");
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
        var stream = Stream("ab", "cd");
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
        var stream = Stream();

        // Act
        int read = stream.Read(new byte[10], 0, 10);

        // Assert
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ReadAsync_ShouldThrowIOException_WhenTheChunksEndBeforeTheLength()
    {
        // Arrange
        var stream = new ResourceStream(Pages("abc"u8.ToArray()), length: 6);
        using var destination = new MemoryStream();

        // Act
        Task act() => stream.CopyToAsync(destination);

        // Assert
        await Assert.ThrowsAsync<IOException>(act);
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeTheOwner_WhenCalled()
    {
        // Arrange
        var owner = new TrackingDisposable();
        var stream = new ResourceStream(Pages("abc"u8.ToArray()), length: 3, owner);
        await stream.ReadAsync(new byte[1]);

        // Act
        await stream.DisposeAsync();

        // Assert
        Assert.True(owner.Disposed);
    }

    [Fact]
    public void ResourceStream_ShouldBeReadOnlyAndNonSeekable_WhenConstructed()
    {
        // Arrange & Act
        var stream = Stream("x");

        // Assert
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);
    }

    [Fact]
    public void ResourceStream_ShouldThrowNotSupported_WhenSeekWriteSetLengthOrPositionIsCalled()
    {
        // Arrange & Act
        var stream = Stream("x");

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
        var stream = Stream("aaa", "bbb");

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
        var stream = Stream("abc");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[10], 0, 10, cts.Token));
    }

    [Fact]
    public async Task CopyToAsync_ShouldStopPartWay_WhenTokenIsCancelledBetweenChunks()
    {
        // Arrange
        var stream = Stream("aaa", "bbb", "ccc");
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

    [Fact]
    public async Task ReadAsync_ShouldFetchEachPageWithThatReadsToken_WhenReadsUseDifferentTokens()
    {
        // Arrange
        var tokens = new List<CancellationToken>();
        var remaining = new Queue<byte[]>(["a"u8.ToArray(), "b"u8.ToArray()]);
        var stream = new ResourceStream(token =>
        {
            tokens.Add(token);
            return Task.FromResult<IReadOnlyList<byte[]>>(remaining.Count > 0 ? [remaining.Dequeue()] : []);
        }, length: 2);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        // Act
        await stream.ReadAsync(new byte[1], 0, 1, first.Token);
        first.Cancel();
        int read = await stream.ReadAsync(new byte[1], 0, 1, second.Token);

        // Assert
        Assert.Equal(1, read);
        Assert.Equal([first.Token, second.Token], tokens);
    }

    [Fact]
    public void Read_ShouldNotDeadlock_WhenCalledUnderASingleThreadedSynchronizationContext()
    {
        // Arrange: a page that arrives asynchronously, read on a thread whose context can't run anything while that
        // thread is blocked in Read, like a UI thread.
        var stream = new ResourceStream(async _ =>
        {
            await Task.Delay(10).ConfigureAwait(false);
            return ["abc"u8.ToArray()];
        }, length: 3);
        int read = -1;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedThreadContext());
            read = stream.Read(new byte[3], 0, 3);
        })
        { IsBackground = true };

        // Act
        thread.Start();
        bool finished = thread.Join(TimeSpan.FromSeconds(10));

        // Assert
        Assert.True(finished, "Read deadlocked waiting for a continuation queued to its own blocked thread.");
        Assert.Equal(3, read);
    }

    /// <summary>Queues posted work for a thread that never gets free to run it.</summary>
    private sealed class BlockedThreadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }

    private sealed class TrackingDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
