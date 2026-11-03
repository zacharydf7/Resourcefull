using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    // What the clients do when the provider leaves out an optional feature: refuse it, changing nothing, rather than
    // seem to succeed. Each test runs only when its Supports switch is off.

    [Fact]
    public async Task SetContainerMetadataAsync_ShouldThrowNotSupportedException_WhenTheProviderDoesNotSupportIt()
    {
        Assert.SkipWhen(SupportsContainerMetadata, "The provider changes container metadata.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ServiceOf(provider).SetContainerMetadataAsync(container.Name, new Dictionary<string, string> { ["a"] = "b" }));
        Assert.Empty((await ServiceOf(provider).GetContainerInfoAsync(container.Name))!.Metadata);
    }

    [Fact]
    public async Task SetResourceMetadataAndContentType_ShouldThrowNotSupportedException_WhenTheProviderDoesNotSupportThem()
    {
        Assert.SkipWhen(SupportsVersionChanges, "The provider changes a version's metadata and content type.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            container.SetResourceMetadataAsync("a", null, new Dictionary<string, string> { ["a"] = "b" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetContentTypeAsync("a", null, "text/plain"));
        ResourceInfo info = (await container.GetResourceInfoAsync("a"))!;
        Assert.Empty(info.Metadata);
        Assert.Null(info.ContentType);
    }

    [Fact]
    public async Task MoveAndRenameResourceAsync_ShouldThrowNotSupportedException_WhenTheProviderDoesNotSupportThem()
    {
        Assert.SkipWhen(SupportsMove, "The provider moves resources.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        IResourceContainerClient other = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => container.MoveResourceAsync("a", other.Name, "a"));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.RenameResourceAsync("a", "b"));
        Assert.Equal(["a"], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
        Assert.Empty(await other.ListResourcesAsync().ToListAsync());
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldThrowNotSupportedExceptionAndLeaveNoContainer_WhenTheProviderDoesNotSupportIt()
    {
        Assert.SkipWhen(SupportsImport, "The provider imports versions.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));
        using var export = new MemoryStream();
        await ServiceOf(provider).ExportContainerAsync(container.Name, export);
        export.Position = 0;
        string restored = NewContainerName();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => ServiceOf(provider).ImportContainerAsync(restored, export));
        Assert.False(await ServiceOf(provider).ContainerExistsAsync(restored));
    }

    [Fact]
    public async Task StartUploadAsync_ShouldThrowNotSupportedException_WhenTheProviderDoesNotSupportUploadSessions()
    {
        Assert.SkipWhen(SupportsUploadSessions, "The provider supports upload sessions.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => container.StartUploadAsync("a"));
        Assert.False(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task SetExpiryAndDeleteExpiredAsync_ShouldThrowNotSupportedException_WhenTheProviderDoesNotSupportExpiry()
    {
        Assert.SkipWhen(SupportsExpiry, "The provider supports expiry.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetExpiryAsync("a", null, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<NotSupportedException>(() => ServiceOf(provider).DeleteExpiredAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => ServiceOf(provider).GetExpiredVersionsAsync());
        Assert.Null((await container.GetResourceInfoAsync("a"))!.ExpiresAt);
    }
}
