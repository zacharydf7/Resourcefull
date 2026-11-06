using Microsoft.Extensions.DependencyInjection;
using Moq;
using Resourcefull.InMemory;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

/// <summary>Changing a version's metadata and content type, and a container's metadata, in place.</summary>
public class ResourcePropertiesTests
{
    [Fact]
    public async Task PropertyMembers_ShouldThrowNotSupported_WhenTheProviderDoesNotImplementThem()
    {
        // Arrange
        ResourceRepository repository = new Mock<ResourceRepository> { CallBase = true }.Object;
        var location = new ResourceAddress("c", "a");
        var metadata = new Dictionary<string, string>();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SetResourceMetadataAsync(location, null, metadata));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SetContentTypeAsync(location, null, "text/plain"));
    }

    [Fact]
    public async Task SetMetadata_ShouldThrowArgumentNull_WhenGivenNoMetadata()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => container.SetResourceMetadataAsync("a", null, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetContainerClient("c").SetMetadataAsync(null!));
    }
}
