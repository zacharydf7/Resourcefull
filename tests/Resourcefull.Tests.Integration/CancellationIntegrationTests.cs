using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Utilities;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Cancellation against a real SQL Server: a canceled upload must leave the store exactly as it was, and a
/// canceled download must stop reading.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class CancellationIntegrationTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task UploadAsync_ShouldLeaveTheStoreUnchanged_WhenCanceledMidStream()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using var provider = fixture.BuildServiceProvider(options => options.UseVersioning(maxVersions: 2));
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var containerName = $"c-{Guid.NewGuid():N}";
        var container = await service.CreateResourceContainerAsync(containerName);
        await container.UploadAsync("doc", new MemoryStream(Encoding.UTF8.GetBytes("v1")));
        await container.UploadAsync("doc", new MemoryStream(Encoding.UTF8.GetBytes("v2")));
        IReadOnlyList<ResourceInfo> versionsBefore = await container.GetResourceVersionsAsync("doc");
        int chunksBefore = await CountChunksAsync(containerName);
        using var cts = new CancellationTokenSource();
        // Canceled partway through the second batch of chunks, once the first has been written to the database.
        const int Batch = ResourceSqlRepository.ChunksPerUploadBatch;
        var content = new CancelAfterReadsStream(new byte[ResourceChunker.ChunkSize * Batch * 2],
            cts, readsBeforeCancel: Batch + 4);

        // Act
        Task act() => container.UploadAsync("doc", content, null, cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);

        IReadOnlyList<ResourceInfo> versionsAfter = await container.GetResourceVersionsAsync("doc");
        Assert.Equal(versionsBefore.Select(v => v.VersionId), versionsAfter.Select(v => v.VersionId));
        Assert.Equal(chunksBefore, await CountChunksAsync(containerName));

        ResourceContent? current = await container.DownloadContentAsync("doc");
        Assert.Equal("v2", current!.Content.ToString());
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldStopReading_WhenTheStreamIsReadWithACanceledToken()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using var provider = fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var container = await service.CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");
        await container.UploadAsync("doc", new MemoryStream(new byte[200_000]));
        ResourceDownloadResult? download = await container.DownloadStreamingAsync("doc", null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Task act() => download!.Stream.ReadAsync(new byte[1024], 0, 1024, cts.Token);

        // Assert
        Assert.NotNull(download);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    [SkippableFact]
    public async Task CreateResourceContainerAsync_ShouldCreateNothing_WhenTokenIsAlreadyCanceled()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using var provider = fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var containerName = $"c-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Task act() => service.CreateResourceContainerAsync(containerName, null, cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.False(await service.ContainerExistsAsync(containerName));
    }

    private async Task<int> CountChunksAsync(string containerName)
    {
        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM ResourceChunks ch
            JOIN Resources r ON r.Id = ch.ResourceId
            WHERE r.ContainerName = @c";
        command.Parameters.AddWithValue("@c", containerName);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
