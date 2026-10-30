using Moq;

namespace Resourcefull.Tests.Unit;

/// <summary>Copies and moves through providers and clients written before they existed.</summary>
public class ResourceCopyTests
{
    private static readonly WrittenContent Written = new(5, "sha256:written", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task CopyResourceAsync_ShouldStoreTheSourcesContentThroughAddResource_WhenAProviderWrittenFor31Copies()
    {
        // Arrange
        var repository = new Mock<IResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetResourceStreamAsync("c", "a", "v1", It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream([1, 2, 3]));
        byte[]? stored = null;
        repository.Setup(r => r.AddResourceAsync("d", It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()))
            .Callback((string _, ResourceInfo _, Stream content, bool _, CancellationToken _) => stored = new BinaryReader(content).ReadBytes(10))
            .ReturnsAsync(Written);
        var destination = new ResourceInfo { ContainerName = "d", Name = "b", CopiedFrom = new ResourceOrigin("c", "a", "v1") };

        // Act
        WrittenContent? written = await repository.Object.CopyResourceAsync(new ResourceLocation("c", "a"), "v1", destination,
            new ResourceWriteCondition { CreateOnly = true });

        // Assert
        Assert.Same(Written, written);
        Assert.Equal([1, 2, 3], stored);
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldReturnNull_WhenAProviderWrittenFor31HasNoSourceVersion()
    {
        // Arrange
        IResourceRepository repository = new Mock<IResourceRepository> { CallBase = true }.Object;

        // Act
        WrittenContent? written = await repository.CopyResourceAsync(new ResourceLocation("c", "a"), "missing",
            new ResourceInfo { ContainerName = "d", Name = "b" }, new ResourceWriteCondition());

        // Assert
        Assert.Null(written);
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldStoreTheVersionsContentThroughAddResource_WhenAProviderWrittenFor31Restores()
    {
        // Arrange
        var repository = new Mock<IResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetResourceInfoAsync("c", "a", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync("c", "a", "v1", It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream([1, 2, 3]));
        byte[]? stored = null;
        repository.Setup(r => r.AddResourceAsync("c", It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), false, It.IsAny<CancellationToken>()))
            .Callback((string _, ResourceInfo _, Stream content, bool _, CancellationToken _) => stored = new BinaryReader(content).ReadBytes(10))
            .ReturnsAsync(Written);
        repository.Setup(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var client = new ResourceContainerClient(repository.Object, null, new ResourcefullOptions { IsVersioningEnabled = true },
            new ResourceContainerInfo { Name = "c" }, null);

        // Act
        ResourceInfo? restored = await client.RestoreVersionAsync("a", "v1");

        // Assert
        Assert.Equal([1, 2, 3], stored);
        Assert.Equal(new ResourceOrigin("c", "a", "v1"), restored!.CopiedFrom);
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldThrowNotSupported_WhenAProviderWrittenFor31IsAsked()
    {
        // Arrange
        IResourceRepository repository = new Mock<IResourceRepository> { CallBase = true }.Object;

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            repository.MoveResourceAsync(new ResourceLocation("c", "a"), new ResourceLocation("c", "b"), null));
    }

    [Fact]
    public async Task ExportImportMembers_ShouldThrowNotSupported_WhenAProviderOrClientWrittenFor31IsAsked()
    {
        // Arrange
        IResourceRepository repository = new Mock<IResourceRepository> { CallBase = true }.Object;
        IResourceServiceClient service = new Mock<IResourceServiceClient> { CallBase = true }.Object;

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            repository.ImportVersionAsync("c", new ResourceInfo { ContainerName = "c", Name = "a" }, new MemoryStream()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ExportContainerAsync("c", new MemoryStream()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ImportContainerAsync("c", new MemoryStream()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.PruneContainerAsync("c"));
    }

    [Fact]
    public async Task CopyAndMove_ShouldThrowNotSupported_WhenAClientWrittenFor31IsAsked()
    {
        // Arrange
        IResourceContainerClient client = new Mock<IResourceContainerClient> { CallBase = true }.Object;

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CopyResourceAsync("a", "c", "b"));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.MoveResourceAsync("a", "c", "b"));
    }
}
