using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Utilities;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// An upload stores its content before the version is visible, in batches that each commit, so nothing waits on it while
/// its content arrives, however slowly. These pause an upload partway, with a batch stored and the rest still to come.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class StagedUploadIntegrationTests(SqlServerFixture fixture)
{
    // Three batches of chunks; the upload pauses in the second, with the first stored.
    private static readonly byte[] Payload =
        new byte[ResourceChunker.ChunkSize * ResourceSqlRepository.ChunksPerUploadBatch * 3];
    private const long PauseAt = ResourceChunker.ChunkSize * (ResourceSqlRepository.ChunksPerUploadBatch + 1L);

    [SkippableFact]
    public async Task UploadAsync_ShouldShowNothingAndLockNothing_WhenItsContentIsStillArriving()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        var content = new PausingStream(Payload, PauseAt);
        Task<ResourceInfo> upload = container.UploadAsync("video.mp4", content);
        await content.Paused;

        // Act: read every way a version shows, and the chunk table with locks that wait for any write in progress.
        ResourceInfo? info = await container.GetResourceInfoAsync("video.mp4");
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("video.mp4");
        IReadOnlyList<ResourceInfo> listed = await container.GetResourcesAsync();
        ResourceContainerUsage usage = await container.GetUsageAsync();
        int chunksSeenWithLocks = await CountChunksWithLocksAsync();
        content.Resume();
        ResourceInfo stored = await upload;

        // Assert
        Assert.Null(info);
        Assert.Empty(versions);
        Assert.Empty(listed);
        Assert.Equal((0L, 0L), (usage.VersionCount, usage.StoredSize.Bytes));
        Assert.True(chunksSeenWithLocks >= ResourceSqlRepository.ChunksPerUploadBatch);
        Assert.Equal(Payload.Length, stored.ContentLength);
        Assert.Equal(stored.VersionId, (await container.GetResourceInfoAsync("video.mp4"))!.VersionId);
    }

    [SkippableFact]
    public async Task DeleteAbandonedUploadsAsync_ShouldDeleteOnlyWhatStoppedBeforeTheTimeGiven_WhenCalled()
    {
        fixture.SkipIfUnavailable();

        // Arrange: an upload paused with a batch stored.
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        using var cts = new CancellationTokenSource();
        var content = new PausingStream(Payload, PauseAt);
        Task<ResourceInfo> upload = container.UploadAsync("video.mp4", content, null, cts.Token);
        await content.Paused;

        // Act: once while it's recent, and once after it last stored anything two hours ago.
        int whileRecent = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddHours(-1));
        await ExecuteAsync("UPDATE [Resources] SET [StagedAt] = DATEADD(hour, -2, [StagedAt]) WHERE [ContainerName] = @c",
            container.Name);
        int onceStale = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddHours(-1));
        cts.Cancel();
        content.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);

        // Assert
        Assert.Equal((0, 1), (whileRecent, onceStale));
        Assert.Equal(0, await StoredRowsAsync(container.Name));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldLeaveNothingBehind_WhenCanceledPartway()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        using var cts = new CancellationTokenSource();
        var content = new PausingStream(Payload, PauseAt);
        Task<ResourceInfo> upload = container.UploadAsync("video.mp4", content, null, cts.Token);
        await content.Paused;
        int whilePaused = await StoredRowsAsync(container.Name);

        // Act
        cts.Cancel();
        content.Resume();

        // Assert: the staged version and its first batch were stored, and are gone.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        Assert.True(whilePaused > ResourceSqlRepository.ChunksPerUploadBatch);
        Assert.Equal(0, await StoredRowsAsync(container.Name));
    }

    private static async Task<IResourceContainerClient> NewContainerAsync(ServiceProvider provider) =>
        await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");

    /// <summary>
    /// Counts every chunk with locking reads that give up after two seconds, so it fails if an upload holds locks on its
    /// chunks while its content arrives.
    /// </summary>
    private async Task<int> CountChunksWithLocksAsync()
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SET LOCK_TIMEOUT 2000; SELECT COUNT(*) FROM [ResourceChunks] WITH (READCOMMITTEDLOCK)";
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Every version row and chunk of the container's, staged or not.</summary>
    private async Task<int> StoredRowsAsync(string containerName)
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM [Resources] WHERE [ContainerName] = @c) + " +
            "(SELECT COUNT(*) FROM [ResourceChunks] ch JOIN [Resources] r ON r.[Id] = ch.[ResourceId] " +
            "WHERE r.[ContainerName] = @c)";
        command.Parameters.AddWithValue("@c", containerName);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, string containerName)
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@c", containerName);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Content that pauses once <paramref name="pauseAt"/> bytes have been read, until <see cref="Resume"/>, as a slow
    /// client's would; a canceled read stops waiting.
    /// </summary>
    private sealed class PausingStream(byte[] content, long pauseAt) : MemoryStream(content)
    {
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanSeek => false;

        public Task Paused => _paused.Task;

        public void Resume() => _resumed.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= pauseAt && !_resumed.Task.IsCompleted)
            {
                _paused.TrySetResult();
                await _resumed.Task;
                cancellationToken.ThrowIfCancellationRequested();
            }
            int count = (int)Math.Min(buffer.Length, Math.Max(0, pauseAt - Position));
            return await base.ReadAsync(Position < pauseAt ? buffer[..count] : buffer, cancellationToken);
        }
    }
}
