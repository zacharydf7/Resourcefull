using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resourcefull.InMemory;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

public class KeyedStoreTests
{
    private sealed class PathUriProvider : IUriProvider
    {
        public string? GetAbsoluteUri(ResourcefullContext context) => $"https://archive.test/{context.ContainerName}";
    }

    private static ServiceProvider TwoStores(Action<ResourcefullOptionsBuilder>? configureArchive = null,
        Action<ResourcefullOptionsBuilder>? configureDefault = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddResourcefull(configureDefault ?? (options => options.UseInMemory()));
        services.AddKeyedResourcefull("archive", configureArchive ?? (options => options.UseInMemory()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddKeyedResourcefull_ShouldKeepEachStoresResourcesApart_WhenTwoStoresAreAdded()
    {
        // Arrange
        await using ServiceProvider provider = TwoStores();
        var hot = provider.GetRequiredService<IResourceServiceClient>();
        var archive = provider.GetRequiredKeyedService<IResourceServiceClient>("archive");

        // Act
        await (await archive.CreateContainerAsync("c")).UploadAsync("a.txt", BinaryData.FromString("old"));

        // Assert
        Assert.True(await archive.ContainerExistsAsync("c"));
        Assert.False(await hot.ContainerExistsAsync("c"));
    }

    [Fact]
    public async Task AddKeyedResourcefull_ShouldApplyEachStoresOwnSettings_WhenTheirSettingsDiffer()
    {
        // Arrange: versioning in the archive only, and a one-resource limit in the hot store only.
        await using ServiceProvider provider = TwoStores(archive => archive.UseInMemory().UseVersioning(),
            hot => hot.UseInMemory().UseContainerPolicies(new ResourceContainerPolicy { MaxResourceCount = 1 }));
        IResourceContainerClient hot = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        IResourceContainerClient archive = await provider.GetRequiredKeyedService<IResourceServiceClient>("archive")
            .CreateContainerAsync("c");
        var replace = new ResourceUploadOptions { Mode = ResourceWriteMode.Replace };

        // Act
        await archive.UploadAsync("a.txt", BinaryData.FromString("1"));
        await archive.UploadAsync("a.txt", BinaryData.FromString("2"), replace);
        await archive.UploadAsync("b.txt", BinaryData.FromString("3"));
        await hot.UploadAsync("a.txt", BinaryData.FromString("1"));
        await hot.UploadAsync("a.txt", BinaryData.FromString("2"), replace);
        Task overLimit() => hot.UploadAsync("b.txt", BinaryData.FromString("3"));

        // Assert
        Assert.Equal(2, (await archive.GetResourceVersionsAsync("a.txt")).Count);
        Assert.Single(await hot.GetResourceVersionsAsync("a.txt"));
        await Assert.ThrowsAsync<ResourcePolicyViolationException>(overLimit);
    }

    [Fact]
    public async Task AddKeyedResourcefull_ShouldGiveOnlyItsOwnStoreItsServices_WhenAStoreConfiguresOne()
    {
        // Arrange
        await using ServiceProvider provider = TwoStores(archive => archive.UseInMemory().UseUriProvider<PathUriProvider>());
        IResourceServiceClient hot = provider.GetRequiredService<IResourceServiceClient>();
        IResourceServiceClient archive = provider.GetRequiredKeyedService<IResourceServiceClient>("archive");

        // Act
        IResourceContainerClient hotContainer = await hot.CreateContainerAsync("c");
        IResourceContainerClient archiveContainer = await archive.CreateContainerAsync("c");

        // Assert
        Assert.Null(hotContainer.Info.Uri);
        Assert.Equal("https://archive.test/c", archiveContainer.Info.Uri);
    }

    [Fact]
    public void AddResourcefull_ShouldThrowAndNameAddKeyedResourcefull_WhenCalledTwice()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());

        // Act
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddResourcefull(options => options.UseInMemory()));

        // Assert
        Assert.Contains("AddKeyedResourcefull", exception.Message);
    }

    [Fact]
    public void AddKeyedResourcefull_ShouldThrowAndNameTheStore_WhenTheNameIsAlreadyAdded()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddKeyedResourcefull("archive", options => options.UseInMemory());

        // Act
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => services.AddKeyedResourcefull("archive", options => options.UseInMemory()));

        // Assert
        Assert.Contains("archive", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddKeyedResourcefull_ShouldThrowArgumentException_WhenGivenABlankName(string name)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>("name", () => new ServiceCollection().AddKeyedResourcefull(name, options => options.UseInMemory()));
    }

    [Fact]
    public void UseExpiry_ShouldRegisterASweepAndIntervalForEachStore_WhenTwoStoresUseIt()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddResourcefull(options => options.UseInMemory().UseExpiry(TimeSpan.FromMinutes(1)));
        services.AddKeyedResourcefull("archive", options => options.UseInMemory().UseExpiry(TimeSpan.FromHours(1)));

        // Assert
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(IHostedService)));
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromMinutes(1), provider.GetRequiredService<ExpirySettings>().Interval);
        Assert.Equal(TimeSpan.FromHours(1), provider.GetRequiredKeyedService<ExpirySettings>("archive").Interval);
    }

    [Fact]
    public async Task UseExpiry_ShouldSweepOnlyItsOwnStore_WhenAStoreHasAName()
    {
        // Arrange: an expired version in each store, and a sweep in the archive only.
        await using ServiceProvider provider = TwoStores(archive => archive.UseInMemory().UseExpiry(TimeSpan.FromHours(1)));
        var expired = new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        IResourceContainerClient hot = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        IResourceContainerClient archive = await provider.GetRequiredKeyedService<IResourceServiceClient>("archive")
            .CreateContainerAsync("c");
        await hot.UploadAsync("a.txt", BinaryData.FromString("hot"), expired);
        await archive.UploadAsync("a.txt", BinaryData.FromString("archive"), expired);
        IHostedService sweep = Assert.Single(provider.GetServices<IHostedService>());

        // Act
        await sweep.StartAsync(CancellationToken.None);
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (await archive.ExistsAsync("a.txt") && DateTime.UtcNow < deadline) await Task.Delay(20);
        await sweep.StopAsync(CancellationToken.None);

        // Assert
        Assert.False(await archive.ExistsAsync("a.txt"));
        Assert.True(await hot.ExistsAsync("a.txt"));
    }

    [Fact]
    public async Task UseProvider_ShouldUseTheProviderTheFactoryMakes_WhenGivenAFactory()
    {
        // Arrange
        var services = new ServiceCollection();
        var repository = new ResourceInMemoryRepository();

        // Act
        services.AddKeyedResourcefull("archive", options => options.UseProvider(_ => repository, ServiceLifetime.Singleton));

        // Assert
        await using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Same(repository, provider.GetRequiredKeyedService<ResourceRepository>("archive"));
    }

    [Fact]
    public void UseProvider_ShouldThrowArgumentNullException_WhenGivenNoFactory()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>("create",
            () => new ResourcefullOptionsBuilder(new ServiceCollection()).UseProvider<ResourceRepository>(null!));
    }

    [Fact]
    public async Task AddResourcefullCheck_ShouldCheckTheStoreNamed_WhenGivenAStoreName()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddKeyedResourcefull("archive", options => options.UseInMemory());
        services.AddHealthChecks().AddResourcefullCheck("archive", storeName: "archive");
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        // Assert
        Assert.Equal(HealthStatus.Healthy, report.Entries["archive"].Status);
    }

    [Fact]
    public async Task AddResourcefullCheck_ShouldThrowAndNameTheStore_WhenNoStoreHasTheName()
    {
        // Arrange: as for the store without a name, a check of a store that isn't added is a mistake in the setup.
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddKeyedResourcefull("archive", options => options.UseInMemory());
        services.AddHealthChecks().AddResourcefullCheck("missing", storeName: "missing");
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Task act() => provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        // Assert
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("missing", exception.Message);
    }
}
