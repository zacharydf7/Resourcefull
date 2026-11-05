using Resourcefull.EntityFramework;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Resourcefull.Sqlite;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Sqlite;

/// <summary>
/// The deletes the contract tests don't reach: the sweep of uploads that stopped partway, and versions with more chunks
/// than one statement deletes.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class DeleteIntegrationTests(SqliteFixture fixture)
{
    // Three batches of chunks; the upload pauses in the second, with the first stored.
    private static readonly byte[] Payload =
        new byte[ResourceChunker.ChunkSize * ResourceSqliteRepository.ChunksPerUploadBatch * 3];
    private const long PauseAt = ResourceChunker.ChunkSize * (ResourceSqliteRepository.ChunksPerUploadBatch + 1L);

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldDeleteAndCountOnlyWhatStoppedBeforeTheTimeGiven_WhenCalled()
    {
        // Arrange: an upload paused with a batch stored.
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        using var cts = new CancellationTokenSource();
        var content = new PausingStream(Payload, PauseAt);
        Task<ResourceInfo> upload = container.UploadAsync("video.mp4", content, null, cts.Token);
        await content.Paused;
        long counted = 0;
        using MeterListener listener = ListenForAbandonedUploads(deleted => Interlocked.Add(ref counted, deleted));

        // Act: once while it's recent, and once after it last stored anything two hours ago.
        int whileRecent = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddHours(-1));
        // Times are kept as their UTC ticks.
        await ExecuteAsync("""UPDATE "Resources" SET "StagedAt" = "StagedAt" - @ticks WHERE "ContainerName" = @c""",
            container.Name, ticks: TimeSpan.FromHours(2).Ticks);
        int onceStale = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddHours(-1));
        cts.Cancel();
        content.Resume();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);

        // Assert: other tests' sweeps may be counted too, so the count is at least this one's.
        Assert.Equal((0, 1), (whileRecent, onceStale));
        Assert.Equal(0, await StoredRowsAsync(container.Name));
        Assert.True(Interlocked.Read(ref counted) >= 1);
    }

    [Fact]
    public async Task CancelUploadAsync_ShouldLeaveAnUploadAlone_WhenGivenTheIdOfOneWhoseContentIsStillArriving()
    {
        // Arrange: an upload's version is staged while its content arrives, as a session's is, but it isn't a session.
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        var content = new PausingStream(Payload, PauseAt);
        Task<ResourceInfo> upload = container.UploadAsync("video.mp4", content);
        await content.Paused;
        string versionId = await StagedVersionIdAsync(container.Name);

        // Act
        ResourceUpload? session = await container.GetUploadAsync("video.mp4", versionId);
        bool canceled = await container.CancelUploadAsync("video.mp4", versionId);
        content.Resume();
        ResourceInfo stored = await upload;

        // Assert
        Assert.Null(session);
        Assert.False(canceled);
        Assert.Equal((versionId, Payload.LongLength), (stored.VersionId, stored.ContentLength!.Value));
    }

    public static TheoryData<string> Deletes => ["version", "name", "container", "expire"];

    [Theory]
    [MemberData(nameof(Deletes))]
    public async Task Delete_ShouldDeleteTheVersionAndItsChunks_WhenItHasMoreChunksThanOneStatementDeletes(string what)
    {
        // Arrange: an older version too, so deleting the name deletes more than one.
        using ServiceProvider provider = fixture.BuildServiceProvider(options => options.UseVersioning());
        var service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("big", BinaryData.FromString("older"));
        ResourceInfo current = await container.UploadAsync("big", BinaryData.FromString("x"),
            new ResourceUploadOptions { Mode = ResourceWriteMode.Replace, ExpiresAt = what == "expire" ? DateTimeOffset.UtcNow.AddDays(-1) : null });
        await ExecuteAsync("""
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < @chunks)
            INSERT INTO "ResourceChunks" ("ResourceId", "Index", "Data")
            SELECT r."Id", n.i + 100, CAST('chunk' AS BLOB)
            FROM "Resources" r CROSS JOIN n
            WHERE r."ContainerName" = @c AND r."Name" = 'big' AND r."IsLatest"
            """, container.Name, ResourceSqliteRepository.ChunksPerDeleteStatement * 2 + 1);

        // Act
        bool deleted = what switch
        {
            "version" => await container.DeleteResourceIfExistsAsync("big", current.VersionId),
            "name" => await container.DeleteResourceIfExistsAsync("big"),
            "expire" => (await provider.GetRequiredService<ResourceRepository>().DeleteExpiredVersionsAsync(
                [(new ResourceAddress(container.Name, "big"), current.VersionId)], DateTimeOffset.UtcNow)).Count == 1,
            _ => await service.DeleteContainerIfExistsAsync(container.Name)
        };

        // Assert: deleting or expiring the version leaves only the older version and its one chunk; deleting the name
        // or the container leaves nothing.
        Assert.True(deleted);
        Assert.Equal(what is "version" or "expire" ? 2 : 0, await StoredRowsAsync(container.Name));
    }

    private static async Task<IResourceContainerClient> NewContainerAsync(ServiceProvider provider) =>
        await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync($"c-{Guid.NewGuid():N}");

    /// <summary>Calls <paramref name="counted"/> with each count of <c>resourcefull.sqlite.abandoned_uploads</c>.</summary>
    private static MeterListener ListenForAbandonedUploads(Action<long> counted)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.sqlite.abandoned_uploads") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => counted(measurement));
        listener.Start();
        return listener;
    }

    /// <summary>The ID of the container's one staged version.</summary>
    private async Task<string> StagedVersionIdAsync(string containerName)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """SELECT "VersionId" FROM "Resources" WHERE "ContainerName" = @c AND "StagedAt" IS NOT NULL""";
        command.Parameters.AddWithValue("@c", containerName);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Every version row and chunk of the container's, staged or not.</summary>
    private async Task<long> StoredRowsAsync(string containerName)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM "Resources" WHERE "ContainerName" = @c)
                + (SELECT COUNT(*) FROM "ResourceChunks" ch JOIN "Resources" r ON r."Id" = ch."ResourceId"
                   WHERE r."ContainerName" = @c)
            """;
        command.Parameters.AddWithValue("@c", containerName);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, string containerName, int chunks = 0, long ticks = 0)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@c", containerName);
        command.Parameters.AddWithValue("@chunks", chunks);
        command.Parameters.AddWithValue("@ticks", ticks);
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
