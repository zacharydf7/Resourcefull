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

    [SkippableFact]
    public async Task UploadAsync_ShouldReturnAndStoreTheMeasuredLength_WhenTheDeclaredLengthIsRightOrAbsent()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var container = await service.CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");
        byte[] payload = new byte[70_000];
        Random.Shared.NextBytes(payload);

        // Act
        ResourceInfo undeclared = await container.UploadAsync("undeclared.bin", new MemoryStream(payload));
        ResourceInfo declared = await container.UploadAsync("declared.bin", new MemoryStream(payload),
            new ResourceUploadOptions { Headers = new ResourceHeaders("application/octet-stream", payload.Length) });
        Exception? wrong = await Record.ExceptionAsync(() => container.UploadAsync("wrong.bin", new MemoryStream(payload),
            new ResourceUploadOptions { Headers = new ResourceHeaders("application/octet-stream", payload.Length + 1) }));

        // Assert
        Assert.Equal(payload.Length, undeclared.ContentLength);
        Assert.Equal(payload.Length, (await container.GetResourceInfoAsync("undeclared.bin"))!.ContentLength);
        Assert.Equal(undeclared.ContentHash, declared.ContentHash);
        Assert.IsType<ResourceContentLengthMismatchException>(wrong);
        Assert.False(await container.ExistsAsync("wrong.bin"));
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
