using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Integration;

[Collection(SqlServerCollection.Name)]
public class ResourceWorkflowIntegrationTests(SqlServerFixture fixture)
{
    private readonly SqlServerFixture _fixture = fixture;

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

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldDeleteEveryVersionAndItsChunks_WhenNoVersionIsGiven()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider(o => o.UseVersioning());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var containerName = $"c-{Guid.NewGuid():N}";
        var container = await service.CreateResourceContainerAsync(containerName);
        await container.UploadAsync("report.txt", BinaryData.FromString("v1"));
        await container.UploadAsync("report.txt", BinaryData.FromString("v2"));
        ResourceInfo older = (await container.GetResourceVersionsAsync("report.txt"))[1];

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("report.txt");

        // Assert
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("report.txt"));
        Assert.False(await container.ExistsAsync("report.txt", older.VersionId));
        Assert.Empty(await container.GetResourceVersionsAsync("report.txt"));
        Assert.Equal(0, await CountChunksAsync(containerName));
    }
}
