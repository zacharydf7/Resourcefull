using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task UploadAsync_ShouldThrowConflict_WhenTheNameExistsWithoutVersioningOrOverwrite()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        Task act() => container.UploadAsync("doc", BinaryData.FromString("v2"));

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal("v1", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldReplaceTheOnlyVersion_WhenOverwritingWithoutVersioning()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        ResourceInfo replacement = await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Overwrite = true });

        // Assert
        ResourceInfo only = Assert.Single(await container.GetResourceVersionsAsync("doc"));
        Assert.Equal(replacement.VersionId, only.VersionId);
        Assert.Equal("v2", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowConflict_WhenCreateOnlyAndTheNameExistsWithVersioning()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        Task act() => container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { CreateOnly = true });

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Single(await container.GetResourceVersionsAsync("doc"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldKeepOneLatestAndTrimToMaxVersions_WhenVersioningIsEnabled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(maxVersions: 2));
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"));
        ResourceInfo third = await container.UploadAsync("doc", BinaryData.FromString("v3"));

        // Assert
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");
        Assert.Equal([third.VersionId, second.VersionId], versions.Select(v => v.VersionId));
        Assert.Single(versions, version => version.IsLatest);
        Assert.Equal("v3", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [SkippableFact]
    public async Task GetResourceVersionsAsync_ShouldListTheCurrentVersionFirstThenNewestToOldest_WhenVersioningIsEnabled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"));
        ResourceInfo third = await container.UploadAsync("doc", BinaryData.FromString("v3"));

        // Act
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");

        // Assert
        Assert.Equal([third.VersionId, second.VersionId, first.VersionId], versions.Select(v => v.VersionId));
        Assert.Equal([true, false, false], versions.Select(v => v.IsLatest));
    }

    [SkippableFact]
    public async Task DownloadContentAsync_ShouldReturnTheRequestedVersion_WhenGivenAnEarlierVersionId()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"));
        await container.UploadAsync("doc", BinaryData.FromString("v2"));

        // Act
        ResourceContent? content = await container.DownloadContentAsync("doc", first.VersionId);

        // Assert
        Assert.Equal("v1", content!.Content.ToString());
        Assert.Equal(first.VersionId, content.ResourceInfo.VersionId);
        Assert.False(content.ResourceInfo.IsLatest);
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldMakeThePreviousVersionCurrent_WhenTheCurrentVersionIsDeleted()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"));
        ResourceInfo third = await container.UploadAsync("doc", BinaryData.FromString("v3"));

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("doc", third.VersionId);

        // Assert
        Assert.True(deleted);
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");
        Assert.Equal(2, versions.Count);
        Assert.Equal(second.VersionId, versions[0].VersionId);
        Assert.Equal([true, false], versions.Select(v => v.IsLatest));
        Assert.Equal("v2", (await container.DownloadContentAsync("doc"))!.Content.ToString());
        Assert.False(await container.DeleteResourceIfExistsAsync("doc", third.VersionId));
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldKeepTheCurrentVersion_WhenGivenAnOlderVersionId()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"));

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("doc", first.VersionId);

        // Assert
        Assert.True(deleted);
        ResourceInfo current = Assert.Single(await container.GetResourceVersionsAsync("doc"));
        Assert.Equal(second.VersionId, current.VersionId);
        Assert.True(current.IsLatest);
        Assert.Equal("v2", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldDeleteEveryVersion_WhenNoVersionIsGiven()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));
        await container.UploadAsync("doc", BinaryData.FromString("v2"));

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("doc");

        // Assert
        Assert.True(deleted);
        Assert.Empty(await container.GetResourceVersionsAsync("doc"));
        Assert.Empty(await container.GetResourcesAsync());
        Assert.False(await container.DeleteResourceIfExistsAsync("doc"));
    }

    [SkippableFact]
    public async Task RestoreVersionAsync_ShouldStoreTheEarlierContentAndMetadataAsANewCurrentVersion_WhenGivenAnEarlierVersionId()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"), new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("text/plain", null),
            Metadata = new Dictionary<string, string> { ["draft"] = "1" }
        });
        await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("text/markdown", null),
            Metadata = new Dictionary<string, string> { ["draft"] = "2", ["reviewed"] = "yes" }
        });

        // Act
        ResourceInfo? restored = await container.RestoreVersionAsync("doc", first.VersionId!);

        // Assert
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");
        Assert.Equal(3, versions.Count);
        Assert.Equal(restored!.VersionId, versions[0].VersionId);
        Assert.NotEqual(first.VersionId, restored.VersionId);
        Assert.Equal(first.ContentHash, restored.ContentHash);
        ResourceContent current = (await container.DownloadContentAsync("doc"))!;
        Assert.Equal("v1", current.Content.ToString());
        Assert.Equal("text/plain", current.ResourceInfo.ContentType);
        Assert.Equal(new Dictionary<string, string> { ["draft"] = "1" }, current.ResourceInfo.Metadata);
    }
}
