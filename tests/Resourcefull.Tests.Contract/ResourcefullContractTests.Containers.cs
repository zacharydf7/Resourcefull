using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task CreateContainerAsync_ShouldStoreTheMetadataAndReturnTheUri_WhenAUriProviderIsConfigured()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseUriProvider<PathUriProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();

        // Act
        IResourceContainerClient container = await service.CreateContainerAsync(name, new Dictionary<string, string> { ["owner"] = "ops" });
        ResourceInfo uploaded = await container.UploadAsync("report.txt", BinaryData.FromString("x"));

        // Assert
        ContainerInfo? stored = await service.GetContainerInfoAsync(name);
        Assert.Equal($"https://resources.test/{name}", stored!.Uri);
        Assert.Equal("ops", stored.Metadata["owner"]);
        ContainerInfo listed = Assert.Single(await service.GetContainersAsync(), c => c.Name == name);
        Assert.Equal(stored.Uri, listed.Uri);
        Assert.Equal("ops", listed.Metadata["owner"]);
        Assert.Equal($"https://resources.test/{name}/report.txt/{uploaded.VersionId}", (await container.GetResourceInfoAsync("report.txt"))!.Uri);
    }

    [SkippableFact]
    public async Task CreateContainerIfNotExistsAsync_ShouldReturnAClientWithTheStoredMetadataAndTheProvidersUri_WhenTheContainerExists()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseUriProvider<PathUriProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        await service.CreateContainerAsync(name, new Dictionary<string, string> { ["owner"] = "ops" });

        // Act
        IResourceContainerClient container = await service.CreateContainerIfNotExistsAsync(name, new Dictionary<string, string> { ["owner"] = "ignored" });

        // Assert
        Assert.Equal("ops", container.Info.Metadata["owner"]);
        Assert.Equal($"https://resources.test/{name}", container.Info.Uri);
    }

    [SkippableFact]
    public async Task CreateContainerAsync_ShouldThrowConflict_WhenTheContainerExists()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        await service.CreateContainerAsync(name);

        // Act
        Task act() => service.CreateContainerAsync(name);

        // Assert: about the container, not a resource in it.
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal((name, null), (exception.ContainerName, exception.ResourceName));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowContainerNotFound_WhenContainerWasNeverCreated()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = ServiceOf(provider).GetContainerClient(NewContainerName());

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
        IResourceContainerClient container = await service.CreateContainerAsync(name);
        await service.DeleteContainerIfExistsAsync(name);

        // Act
        Task act() => container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(act);
        Assert.Null(await container.GetResourceInfoAsync("doc"));
    }

    [SkippableFact]
    public async Task DeleteContainerIfExistsAsync_ShouldLeaveNothingBehind_WhenTheNameIsReused()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        IResourceContainerClient container = await service.CreateContainerAsync(name);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        bool deleted = await service.DeleteContainerIfExistsAsync(name);
        await service.CreateContainerAsync(name);

        // Assert
        Assert.True(deleted);
        Assert.Empty(await container.ListResourcesAsync().ToListAsync());
        Assert.Null(await container.DownloadContentAsync("doc"));
    }

    // Empty, and at and beyond the SQL Server provider's cap on resources per delete batch.
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(250)]
    public async Task DeleteContainerIfExistsAsync_ShouldDeleteEveryResource_WhenGivenAContainerHoldingThisMany(int resources)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        IResourceContainerClient container = await service.CreateContainerAsync(name);
        for (int i = 0; i < resources; i++)
        {
            await container.UploadAsync($"r{i}", BinaryData.FromString("x"));
        }

        // Act
        bool deleted = await service.DeleteContainerIfExistsAsync(name);

        // Assert
        Assert.True(deleted);
        Assert.False(await service.ContainerExistsAsync(name));
        await service.CreateContainerAsync(name);
        Assert.Empty(await container.ListResourcesAsync().ToListAsync());
    }

    private sealed class PathUriProvider : IUriProvider
    {
        public string? GetAbsoluteUri(ResourcefullContext context) => context.ResourceName is null
            ? $"https://resources.test/{context.ContainerName}"
            : $"https://resources.test/{context.ContainerName}/{context.ResourceName}/{context.ResourceVersionId}";
    }
}
