using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Content moves through the SQL Server provider a little at a time: memory stays bounded whatever the size, and a
/// version is invisible, and holds no lock on its name, until all of it is stored.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class StreamingIntegrationTests(SqlServerFixture fixture)
{
    private const long LargePayload = 64L * 1024 * 1024 + 12_345;
    private const long MemoryAllowance = 16L * 1024 * 1024;

    [SkippableFact]
    public async Task UploadAndDownload_ShouldKeepLiveMemoryBounded_WhenTheContentIsMuchLargerThanTheAllowance()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");
        await container.GetResourcesAsync();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        var upload = new GeneratedStream(LargePayload, sampleMemory: true);
        var download = new VerifyingStream(sampleMemory: true);

        // Act
        await container.UploadAsync("large.bin", upload);
        ResourceDownloadResult? result = await container.DownloadStreamingAsync("large.bin");
        await using (Stream stream = result!.Stream)
        {
            await stream.CopyToAsync(download);
        }

        // Assert
        Assert.Equal(LargePayload, download.Length);
        Assert.True(upload.PeakMemory - baseline < MemoryAllowance, $"Uploading held {(upload.PeakMemory - baseline) / 1024 / 1024} MB.");
        Assert.True(download.PeakMemory - baseline < MemoryAllowance, $"Downloading held {(download.PeakMemory - baseline) / 1024 / 1024} MB.");
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldReturnTheExactContent_WhenItSpansManyChunksAndEndsMidChunk()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        const long size = 5L * 1024 * 1024 + 123;
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");
        ResourceInfo uploaded = await container.UploadAsync("file.bin", new GeneratedStream(size));
        var download = new VerifyingStream();

        // Act
        ResourceDownloadResult? result = await container.DownloadStreamingAsync("file.bin");
        await using (Stream stream = result!.Stream)
        {
            Assert.Equal(size, stream.Length);
            await stream.CopyToAsync(download);
        }

        // Assert
        Assert.Equal(size, uploaded.ContentLength);
        Assert.Equal(size, download.Length);
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldReturnTheChunksInIndexOrder_WhenTheyWereStoredOutOfOrderFromZeroAndOfAnySize()
    {
        fixture.SkipIfUnavailable();

        // Arrange: chunks numbered from zero, as the first release numbered them, stored out of order, with one bigger
        // than the chunk size uploads use.
        using ServiceProvider provider = fixture.BuildServiceProvider();
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync(containerName);
        await container.UploadAsync("n", new MemoryStream());
        await using (SqlConnection connection = await fixture.OpenConnectionAsync())
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.CommandText = @"
                INSERT INTO ResourceChunks (ResourceId, [Index], Data)
                SELECT r.Id, chunk.[Index], chunk.Data
                FROM Resources r
                CROSS JOIN (VALUES
                    (2, CAST('ccc' AS varbinary(max))),
                    (0, CAST('aaa' AS varbinary(max))),
                    (1, CAST(REPLICATE(CAST('b' AS varchar(max)), 70000) AS varbinary(max)))) chunk([Index], Data)
                WHERE r.ContainerName = @c AND r.Name = 'n'";
            command.Parameters.AddWithValue("@c", containerName);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        ResourceDownloadResult? download = await container.DownloadStreamingAsync("n");
        await using Stream stream = download!.Stream;
        string content = await new StreamReader(stream).ReadToEndAsync();

        // Assert
        Assert.Equal("aaa" + new string('b', 70000) + "ccc", content);
        Assert.Equal(70006, stream.Length);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldNeitherShowTheVersionNorHoldUpOtherWritersOfTheName_WhileItsContentIsStillArriving()
    {
        fixture.SkipIfUnavailable();

        // Arrange: an upload that stops partway through its content until released.
        using ServiceProvider provider = fixture.BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");
        var paused = new GeneratedStream(3L * 1024 * 1024, pauseAfter: 1024 * 1024);
        Task<ResourceInfo> slowUpload = container.UploadAsync("report.bin", paused);
        Assert.True(await paused.Paused.WaitAsync(TimeSpan.FromSeconds(30)), "The upload never reached its pause.");

        // Act
        IReadOnlyList<ResourceInfo> visibleWhilePaused = await container.GetResourceVersionsAsync("report.bin");
        ResourceInfo other = await container.UploadAsync("report.bin", BinaryData.FromString("written meanwhile")).WaitAsync(TimeSpan.FromSeconds(30));
        paused.Release();
        ResourceInfo slow = await slowUpload.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Empty(visibleWhilePaused);
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("report.bin");
        Assert.Equal([slow.VersionId, other.VersionId], versions.Select(v => v.VersionId));
        Assert.True(versions[0].IsLatest);
    }

    /// <summary>
    /// Deterministic, non-seekable content that never exists in memory all at once. It can sample the live heap as it
    /// is read, and pause partway until released.
    /// </summary>
    private sealed class GeneratedStream(long length, bool sampleMemory = false, long pauseAfter = -1) : Stream
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _paused = new(0);
        private long _position;
        private long _nextSample;

        public long PeakMemory { get; private set; }
        public SemaphoreSlim Paused => _paused;
        public void Release() => _release.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position == pauseAfter)
            {
                _paused.Release();
                await _release.Task.WaitAsync(cancellationToken);
            }

            long limit = pauseAfter > _position ? pauseAfter : length;
            int count = (int)Math.Min(buffer.Length, limit - _position);
            Span<byte> span = buffer.Span;
            for (int i = 0; i < count; i++) span[i] = ContentAt(_position + i);
            _position += count;

            if (sampleMemory && _position >= _nextSample)
            {
                PeakMemory = Math.Max(PeakMemory, GC.GetTotalMemory(forceFullCollection: true));
                _nextSample += 4 * 1024 * 1024;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Checks every byte written against the generated content, keeping none of it.</summary>
    private sealed class VerifyingStream(bool sampleMemory = false) : Stream
    {
        private long _length;
        private long _nextSample;

        public long PeakMemory { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i] != ContentAt(_length + i)) throw new InvalidDataException($"Byte {_length + i} differs from what was uploaded.");
            }
            _length += buffer.Length;

            if (sampleMemory && _length >= _nextSample)
            {
                PeakMemory = Math.Max(PeakMemory, GC.GetTotalMemory(forceFullCollection: true));
                _nextSample += 4 * 1024 * 1024;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte ContentAt(long position) => (byte)(position * 31 + (position >> 16));
}
