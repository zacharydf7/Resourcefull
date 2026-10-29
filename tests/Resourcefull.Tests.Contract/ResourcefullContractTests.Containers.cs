using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task CreateResourceContainerAsync_ShouldStoreTheMetadataAndUri_WhenAUriProviderIsConfigured()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.SetUriProvider<PathUriProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();

        // Act
        IResourceContainerClient container = await service.CreateResourceContainerAsync(name, new Dictionary<string, string> { ["owner"] = "ops" });
        ResourceInfo uploaded = await container.UploadAsync("report.txt", BinaryData.FromString("x"));

        // Assert
        ResourceContainerInfo? stored = await service.GetResourceContainerInfoAsync(name);
        Assert.Equal($"https://resources.test/{name}", stored!.Uri);
        Assert.Equal("ops", stored.Metadata["owner"]);
        ResourceContainerInfo listed = Assert.Single(await service.GetResourceContainersAsync(), c => c.Name == name);
        Assert.Equal(stored.Uri, listed.Uri);
        Assert.Equal("ops", listed.Metadata["owner"]);
        Assert.Equal($"https://resources.test/{name}/report.txt/{uploaded.VersionId}", (await container.GetResourceInfoAsync("report.txt"))!.Uri);
    }

    [SkippableFact]
    public async Task CreateResourceContainerAsync_ShouldThrowConflict_WhenTheContainerExists()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        await service.CreateResourceContainerAsync(name);

        // Act
        Task act() => service.CreateResourceContainerAsync(name);

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowContainerNotFound_WhenContainerWasNeverCreated()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = ServiceOf(provider).GetResourceContainerClient(NewContainerName());

        // Act
        Task act() => container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(act);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowContainerNotFound_WhenContainerIsDeletedAfterTheClientIsObtained()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        IResourceContainerClient container = await service.CreateResourceContainerAsync(name);
        await service.DeleteResourceContainerIfExistsAsync(name);

        // Act
        Task act() => container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(act);
        Assert.Null(await container.GetResourceInfoAsync("doc"));
    }

    [SkippableFact]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldLeaveNothingBehind_WhenTheNameIsReused()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        IResourceContainerClient container = await service.CreateResourceContainerAsync(name);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        bool deleted = await service.DeleteResourceContainerIfExistsAsync(name);
        await service.CreateResourceContainerAsync(name);

        // Assert
        Assert.True(deleted);
        Assert.Empty(await container.GetResourcesAsync());
        Assert.Null(await container.DownloadContentAsync("doc"));
    }

    // Empty, and at and beyond the SQL Server provider's cap on resources per delete batch.
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(250)]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldDeleteEveryResource_WhenGivenAContainerHoldingThisMany(int resources)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        IResourceContainerClient container = await service.CreateResourceContainerAsync(name);
        for (int i = 0; i < resources; i++)
        {
            await container.UploadAsync($"r{i}", BinaryData.FromString("x"));
        }

        // Act
        bool deleted = await service.DeleteResourceContainerIfExistsAsync(name);

        // Assert
        Assert.True(deleted);
        Assert.False(await service.ContainerExistsAsync(name));
        await service.CreateResourceContainerAsync(name);
        Assert.Empty(await container.GetResourcesAsync());
    }

    private sealed class PathUriProvider : IUriProvider
    {
        public string? GetAbsoluteUri(ResourcefullContext context) => context.ResourceName is null
            ? $"https://resources.test/{context.ContainerName}"
            : $"https://resources.test/{context.ContainerName}/{context.ResourceName}/{context.ResourceVersionId}";
    }
}
