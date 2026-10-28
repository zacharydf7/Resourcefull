using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Resourcefull.Tests.Integration;

[Collection(SqlServerCollection.Name)]
public class ResourceWorkflowIntegrationTests(SqlServerFixture fixture)
{
    private readonly SqlServerFixture _fixture = fixture;

    [SkippableFact]
    public async Task ResourceContainerClient_ShouldRoundTripAResource_WhenUploadedThenDownloadedAndListed()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();

        var containerName = $"c-{Guid.NewGuid():N}";
        await service.CreateResourceContainerAsync(containerName);
        var container = service.GetResourceContainerClient(containerName);
        var payload = Encoding.UTF8.GetBytes("integration round-trip payload");

        // Act
        await container.UploadAsync("artifact.txt", new MemoryStream(payload));
        var content = await container.DownloadContentAsync("artifact.txt");
        var resources = await container.GetResourcesAsync();

        // Assert
        Assert.NotNull(content);
        Assert.Equal(payload, content!.Content.ToArray());
        var single = Assert.Single(resources);
        Assert.Equal("artifact.txt", single.Name);
        Assert.True(single.IsLatest);
    }

    [SkippableFact]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldCascadeDeleteResourcesAndChunks_WhenContainerHoldsAResource()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();

        var containerName = $"c-{Guid.NewGuid():N}";
        await service.CreateResourceContainerAsync(containerName);
        var container = service.GetResourceContainerClient(containerName);
        await container.UploadAsync("artifact.bin", new MemoryStream(new byte[200_000]));
        Assert.True(await CountChunksAsync(containerName) > 0);

        // Act
        var deleted = await service.DeleteResourceContainerIfExistsAsync(containerName);

        // Assert
        Assert.True(deleted);
        Assert.False(await service.ContainerExistsAsync(containerName));
        Assert.Equal(0, await CountChunksAsync(containerName));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldKeepOneLatestAndTrimToMaxVersions_WhenVersioningIsEnabled()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider(options => options.UseVersioning(maxVersions: 2));
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();

        var containerName = $"c-{Guid.NewGuid():N}";
        await service.CreateResourceContainerAsync(containerName);
        var container = service.GetResourceContainerClient(containerName);

        // Act
        await container.UploadAsync("doc", new MemoryStream(Encoding.UTF8.GetBytes("v1")));
        await container.UploadAsync("doc", new MemoryStream(Encoding.UTF8.GetBytes("v2")));
        await container.UploadAsync("doc", new MemoryStream(Encoding.UTF8.GetBytes("v3")));

        // Assert
        var versions = await container.GetResourceVersionsAsync("doc");
        Assert.Equal(2, versions.Count);
        Assert.Single(versions, version => version.IsLatest);

        var current = await container.DownloadContentAsync("doc");
        Assert.Equal("v3", current!.Content.ToString());
    }

    private async Task<int> CountChunksAsync(string containerName)
    {
        await using var connection = await _fixture.OpenConnectionAsync();
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
