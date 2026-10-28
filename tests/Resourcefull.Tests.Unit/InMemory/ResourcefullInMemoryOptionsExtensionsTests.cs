using Microsoft.Extensions.DependencyInjection;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit.InMemory;

public class ResourcefullInMemoryOptionsExtensionsTests
{
    [Fact]
    public void UseInMemory_ShouldThrowAnException_WhenProviderIsAlreadyConfigured()
    {
        // Arrange & Act
        var services = new ServiceCollection();
        var resourcefullOptionsBuilder = new ResourcefullOptionsBuilder(services)
        {
            ProviderConfigured = true
        };

        // Assert
        Assert.Throws<InvalidOperationException>(() => resourcefullOptionsBuilder.UseInMemory());
    }

    [Fact]
    public void UseInMemory_ShouldAddResourceRepository_WhenProviderIsConfigured()
    {
        // Arrange
        var services = new ServiceCollection();
        var resourcefullOptionsBuilder = new ResourcefullOptionsBuilder(services);

        // Act
        resourcefullOptionsBuilder.UseInMemory();

        // Assert
        Assert.True(resourcefullOptionsBuilder.ProviderConfigured, "Failure: Provider should be configured.");
        Assert.Single(services.ToList());
        Assert.Equal(typeof(IResourceRepository), services.Single().ServiceType);
    }
}