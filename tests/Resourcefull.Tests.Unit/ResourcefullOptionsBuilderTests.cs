using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Resourcefull.Providers;

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
        Assert.Equal(7, options.MaxVersions);
    }

    [Fact]
    public void UseUriProvider_ShouldRegisterProvider_WhenCalledOnce()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        new ResourcefullOptionsBuilder(services).UseUriProvider<FakeUriProvider>();

        // Assert
        IUriProvider? provider = services.BuildServiceProvider().GetService<IUriProvider>();
        Assert.IsType<FakeUriProvider>(provider);
    }

    [Fact]
    public void UseUriProvider_ShouldReplaceExistingProvider_WhenCalledTwice()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new ResourcefullOptionsBuilder(services);

        // Act
        builder.UseUriProvider<FakeUriProvider>();
        builder.UseUriProvider<FakeUriProvider>();

        // Assert
        Assert.NotNull(services.SingleOrDefault(d => d.ServiceType == typeof(IUriProvider)));
    }

    /// <summary>A provider defined outside the library, knowing one container and nothing else.</summary>
    private sealed class OneContainerProvider : ResourceRepository
    {
        public override Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(resourceContainerName == "custom" ? new ContainerInfo { Name = "custom" } : null);

        public override Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
            ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<IReadOnlyList<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceLocation resource,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<ResourceInfo?> GetResourceInfoAsync(ResourceLocation resource, string? versionId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<bool> DeleteResourceAsync(ResourceLocation resource, string? versionId = null,
            ResourceConditions? conditions = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task AddContainerAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<IReadOnlyList<ContainerInfo>> GetAllContainerInfoAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public override Task<bool> DeleteContainerAsync(string resourceContainerName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
        ServiceDescriptor descriptor = Assert.Single(services, d => d.ServiceType == typeof(ResourceRepository));
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
