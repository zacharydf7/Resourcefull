using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Resourcefull.Tests.Unit;

public class IServiceCollectionExtensionsTests
{
    [Fact]
    public void AddResourcefull_ShouldThrowAnException_WhenNoProviderIsConfigured()
    {
        // Arrange & Act
        var services = new ServiceCollection();

        // Assert
        Assert.Throws<InvalidOperationException>(() => services.AddResourcefull(options => { }));
    }

    [Fact]
    public void AddResourcefull_ShouldAddResourceServiceClient_WhenProviderIsConfigured()
    {
        // Arrange
        var services = new ServiceCollection();

        // Mock
        var resourcefullOptionsBuilderAction = new Mock<Action<ResourcefullOptionsBuilder>>();
        resourcefullOptionsBuilderAction.Setup(action => action.Invoke(It.IsAny<ResourcefullOptionsBuilder>()))
            .Callback((ResourcefullOptionsBuilder options) => options.ProviderConfigured = true);

        // Act
        services.AddResourcefull(resourcefullOptionsBuilderAction.Object);

        // Assert
        Assert.NotNull(services.SingleOrDefault(x => x.ServiceType == typeof(IResourceServiceClient)));
    }
}