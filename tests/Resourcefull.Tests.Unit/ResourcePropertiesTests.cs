using Microsoft.Extensions.DependencyInjection;
using Moq;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit;

/// <summary>Changing a version's metadata and content type, and a container's metadata, in place.</summary>
public class ResourcePropertiesTests
{
    [Fact]
    public async Task PropertyMembers_ShouldThrowNotSupported_WhenTheProviderOrClientDoesNotImplementThem()
    {
        // Arrange
        ResourceRepository repository = new Mock<ResourceRepository> { CallBase = true }.Object;
        IResourceContainerClient container = new Mock<IResourceContainerClient> { CallBase = true }.Object;
        IResourceServiceClient service = new Mock<IResourceServiceClient> { CallBase = true }.Object;
        var location = new ResourceLocation("c", "a");
        var metadata = new Dictionary<string, string>();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SetResourceMetadataAsync(location, null, metadata));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SetContentTypeAsync(location, null, "text/plain"));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetResourceMetadataAsync("a", null, metadata));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetContentTypeAsync("a", null, "text/plain"));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.SetResourceContainerMetadataAsync("c", metadata));
    }

    [Fact]
    public async Task SetMetadata_ShouldThrowArgumentNull_WhenGivenNoMetadata()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateResourceContainerAsync("c");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => container.SetResourceMetadataAsync("a", null, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.SetResourceContainerMetadataAsync("c", null!));
    }
}
