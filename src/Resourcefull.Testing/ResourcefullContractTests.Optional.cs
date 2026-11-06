using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    // What the clients do when the provider leaves out an optional feature: refuse it, changing nothing, rather than
    // seem to succeed. Each test runs only when the feature's flag or switch is off.

    [Fact]
    public async Task SetMetadataAsync_ShouldThrowNotSupportedException_WhenTheProviderDoesNotSupportIt()
    {
        Assert.SkipWhen(SupportsContainerMetadata, "The provider changes container metadata.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ServiceOf(provider).GetContainerClient(container.Name).SetMetadataAsync(new Dictionary<string, string> { ["a"] = "b" }));
        Assert.Empty((await ServiceOf(provider).GetContainerClient(container.Name).GetInfoAsync())!.Metadata);
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
    public async Task RetentionAndLegalHolds_ShouldThrowNotSupportedExceptionAndStoreNothing_WhenTheProviderDoesNotSupportThem()
    {
        Assert.SkipWhen(SupportsRetention, "The provider supports retention.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));
        using ServiceProvider retainingAll = BuildServiceProvider(o =>
            o.UseContainerPolicies(new ResourceContainerPolicy { RetainFor = TimeSpan.FromDays(1) }));

        // Act & Assert: a version the provider would leave unprotected isn't stored.
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetRetentionAsync("a", null, DateTimeOffset.UtcNow.AddDays(1)));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetLegalHoldAsync("a", null, true));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            container.UploadAsync("b", BinaryData.FromString("b"), new ResourceUploadOptions { LegalHold = true }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            container.CopyResourceAsync("a", container.Name, "c", new ResourceCopyOptions { RetainUntil = DateTimeOffset.UtcNow.AddDays(1) }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            ServiceOf(retainingAll).GetContainerClient(container.Name).UploadAsync("d", BinaryData.FromString("d")));
        Assert.Equal(["a"], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task TheBin_ShouldThrowNotSupportedExceptionAndDeleteNothing_WhenTheProviderDoesNotSupportIt()
    {
        Assert.SkipWhen(SupportsSoftDelete, "The provider keeps deleted versions.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o =>
            o.UseExpiry().UseContainerPolicies(new ResourceContainerPolicy { KeepDeletedFor = TimeSpan.FromDays(1) }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));

        // Act & Assert: a delete meant to be kept isn't made for good.
        await Assert.ThrowsAsync<NotSupportedException>(() => container.DeleteResourceIfExistsAsync("a"));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.ListDeletedAsync().ToListAsync().AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(() => container.UndeleteAsync("a"));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.PurgeDeletedAsync("a"));
        Assert.Equal(0, await ServiceOf(provider).PurgeDueAsync());
        Assert.True(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task Expiry_ShouldThrowNotSupportedExceptionAndStoreNothing_WhenTheProviderDoesNotSupportIt()
    {
        Assert.SkipWhen(SupportsExpiry, "The provider supports expiry.");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddDays(1);

        // Act & Assert: a version with an expiry that would never happen isn't stored.
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetExpiryAsync("a", null, expiresAt));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            container.UploadAsync("b", BinaryData.FromString("b"), new ResourceUploadOptions { ExpiresAt = expiresAt }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            container.CopyResourceAsync("a", container.Name, "c", new ResourceCopyOptions { ExpiresAt = expiresAt }));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            container.StartUploadAsync("d", new ResourceUploadSessionOptions { ExpiresAt = expiresAt }));
        await Assert.ThrowsAsync<NotSupportedException>(() => ServiceOf(provider).DeleteExpiredAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => ServiceOf(provider).GetExpiredVersionsAsync());
        Assert.Equal(["a"], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
        Assert.Null((await container.GetResourceInfoAsync("a"))!.ExpiresAt);
    }
}
