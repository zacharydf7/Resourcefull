using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Resourcefull.Tests.Unit;

public class ResourcefullOptionsBuilderTests
{
    private sealed class FakeUriProvider : IUriProvider
    {
        public string? GetAbsoluteUri(ResourcefullContext context) => null;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UseVersioning_ShouldThrow_WhenMaxVersionsIsNotPositive(int maxVersions)
    {
        // Arrange & Act
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Assert
        Assert.Throws<InvalidOperationException>(() => builder.UseVersioning(maxVersions));
    }

    [Fact]
    public void UseVersioning_ShouldEnableVersioningWithDefaultMax_WhenNoValueProvided()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        new ResourcefullOptionsBuilder(services).UseVersioning();

        // Assert
        ResourcefullOptions options = services.BuildServiceProvider().GetRequiredService<IOptions<ResourcefullOptions>>().Value;
        Assert.True(options.IsVersioningEnabled);
        Assert.Equal(100, options.MaxVersions);
    }

    [Fact]
    public void UseVersioning_ShouldSetMaxVersions_WhenValueProvided()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        new ResourcefullOptionsBuilder(services).UseVersioning(7);

        // Assert
        ResourcefullOptions options = services.BuildServiceProvider().GetRequiredService<IOptions<ResourcefullOptions>>().Value;
        Assert.True(options.IsVersioningEnabled);
        Assert.Equal(7, options.MaxVersions);
    }

    [Fact]
    public void SetUriProvider_ShouldRegisterProvider_WhenCalledOnce()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        new ResourcefullOptionsBuilder(services).SetUriProvider<FakeUriProvider>();

        // Assert
        IUriProvider? provider = services.BuildServiceProvider().GetService<IUriProvider>();
        Assert.IsType<FakeUriProvider>(provider);
    }

    [Fact]
    public void SetUriProvider_ShouldReplaceExistingProvider_WhenCalledTwice()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new ResourcefullOptionsBuilder(services);

        // Act
        builder.SetUriProvider<FakeUriProvider>();
        builder.SetUriProvider<FakeUriProvider>();

        // Assert
        Assert.NotNull(services.SingleOrDefault(d => d.ServiceType == typeof(IUriProvider)));
    }
}
