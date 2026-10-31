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
        Assert.Throws<ArgumentOutOfRangeException>("maxVersions", () => builder.UseVersioning(maxVersions));
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

    /// <summary>A provider defined outside the library, knowing one container and nothing else.</summary>
    private sealed class OneContainerProvider : IResourceRepository
    {
        public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default)
            => Task.FromResult(resourceContainerName == "custom" ? new ResourceContainerInfo { Name = "custom" } : null);

        public Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(ServiceLifetime.Singleton)]
    [InlineData(ServiceLifetime.Scoped)]
    public void UseProvider_ShouldRegisterTheRepositoryWithTheLifetimeGiven_WhenNoProviderIsConfigured(ServiceLifetime lifetime)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var builder = new ResourcefullOptionsBuilder(services).UseProvider<OneContainerProvider>(lifetime);

        // Assert
        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(IResourceRepository));
        Assert.Equal(typeof(OneContainerProvider), descriptor.ImplementationType);
        Assert.Equal(lifetime, descriptor.Lifetime);
        Assert.True(builder.ProviderConfigured);
    }

    [Fact]
    public void UseProvider_ShouldThrow_WhenAProviderIsAlreadyConfigured()
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection()).UseProvider<OneContainerProvider>();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => builder.UseProvider<OneContainerProvider>());
    }

    [Fact]
    public async Task UseProvider_ShouldServeTheServiceClientFromACustomProvider_WhenRegisteredThroughAddResourcefull()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseProvider<OneContainerProvider>());
        using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        bool exists = await provider.GetRequiredService<IResourceServiceClient>().ContainerExistsAsync("custom");

        // Assert
        Assert.True(exists);
    }
}
