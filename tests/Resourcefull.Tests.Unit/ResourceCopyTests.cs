using Moq;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

/// <summary>Copies and moves through providers and clients written before they existed.</summary>
public class ResourceCopyTests
{
    private static readonly WrittenContent Written = new(5, "sha256:written", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task CopyResourceAsync_ShouldStoreTheSourcesContentThroughAddResource_WhenTheProviderDoesNotOverrideIt()
    {
        // Arrange
        var repository = new Mock<ResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetResourceStreamAsync(new ResourceLocation("c", "a"), "v1", It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream([1, 2, 3]));
        byte[]? stored = null;
        repository.Setup(r => r.AddResourceAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.Is<ResourceWriteCondition?>(c => c != null && c.CreateOnly), It.IsAny<CancellationToken>()))
            .Callback((NewResourceVersion _, Stream content, ResourceWriteCondition? _, CancellationToken _) =>
                stored = new BinaryReader(content).ReadBytes(10))
            .ReturnsAsync(Written);
        var destination = new NewResourceVersion(new ResourceLocation("d", "b"), "v2") { CopiedFrom = new ResourceOrigin("c", "a", "v1") };

        // Act
        WrittenContent? written = await repository.Object.CopyResourceAsync(new ResourceLocation("c", "a"), "v1", destination,
            new ResourceWriteCondition { CreateOnly = true });

        // Assert
        Assert.Same(Written, written);
        Assert.Equal([1, 2, 3], stored);
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldReturnNull_WhenTheProviderDoesNotOverrideItAndHasNoSourceVersion()
    {
        // Arrange
        ResourceRepository repository = new Mock<ResourceRepository> { CallBase = true }.Object;

        // Act
        WrittenContent? written = await repository.CopyResourceAsync(new ResourceLocation("c", "a"), "missing",
            new NewResourceVersion(new ResourceLocation("d", "b"), "v2"));

        // Assert
        Assert.Null(written);
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldStoreTheVersionsContentThroughAddResource_WhenTheProviderDoesNotOverrideCopy()
    {
        // Arrange
        var repository = new Mock<ResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceLocation("c", "a"), "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(new ResourceLocation("c", "a"), "v1", It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream([1, 2, 3]));
        byte[]? stored = null;
        repository.Setup(r => r.AddResourceAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.Is<ResourceWriteCondition?>(c => c == null || !c.CreateOnly), It.IsAny<CancellationToken>()))
            .Callback((NewResourceVersion _, Stream content, ResourceWriteCondition? _, CancellationToken _) =>
                stored = new BinaryReader(content).ReadBytes(10))
            .ReturnsAsync(Written);
        repository.Setup(r => r.GetResourceVersionsAsync(new ResourceLocation("c", "a"), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var client = new ResourceContainerClient(repository.Object, null, new ResourcefullOptions { MaxVersions = 100 },
            "c", null);

        // Act
        ResourceInfo? restored = await client.RestoreVersionAsync("a", "v1");

        // Assert
        Assert.Equal([1, 2, 3], stored);
        Assert.Equal(new ResourceOrigin("c", "a", "v1"), restored!.CopiedFrom);
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldThrowNotSupported_WhenTheProviderDoesNotOverrideIt()
    {
        // Arrange
        ResourceRepository repository = new Mock<ResourceRepository> { CallBase = true }.Object;

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            repository.MoveResourceAsync(new ResourceLocation("c", "a"), new ResourceLocation("c", "b"), null));
    }

    [Fact]
    public async Task ExportImportMembers_ShouldThrowNotSupported_WhenTheProviderOrClientDoesNotImplementThem()
    {
        // Arrange
        ResourceRepository repository = new Mock<ResourceRepository> { CallBase = true }.Object;
        IResourceServiceClient service = new Mock<IResourceServiceClient> { CallBase = true }.Object;

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            repository.ImportVersionAsync(new NewResourceVersion(new ResourceLocation("c", "a"), "v1"), new MemoryStream()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ExportContainerAsync("c", new MemoryStream()));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ImportContainerAsync("c", new MemoryStream()));
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
