using Microsoft.Extensions.DependencyInjection;
using Resourcefull.InMemory;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Unit.SqlServer;
public class ResourcefullSqlOptionsExtensionsTests
{
    [Fact]
    public void UseSqlServer_ShouldThrowAnException_WhenProviderIsAlreadyConfigured()
    {
        // Arrange & Act
        var services = new ServiceCollection();
        var resourcefullOptionsBuilder = new ResourcefullOptionsBuilder(services);
        resourcefullOptionsBuilder.UseInMemory();

        // Assert
        Assert.Throws<InvalidOperationException>(() => resourcefullOptionsBuilder.UseSqlServer("someConnectionString"));
    }

    [Fact]
    public async Task UseSqlServer_ShouldProvideTheSqlRepositoryAndBackfill_WhenProviderIsConfigured()
    {
        // Arrange
        var services = new ServiceCollection();
        var resourcefullOptionsBuilder = new ResourcefullOptionsBuilder(services);

        // Act
        resourcefullOptionsBuilder.UseSqlServer("Server=unused;Database=unused");

        // Assert
        Assert.True(resourcefullOptionsBuilder.ProviderConfigured, "Failure: Provider should be configured.");
        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        Assert.IsType<ResourceSqlRepository>(scope.ServiceProvider.GetRequiredService<IResourceRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ContentHashBackfill>());
    }

    [Fact]
    public async Task MigrateResourcefullDatabaseAsync_ShouldThrowAndNameUseSqlServer_WhenResourcefullUsesAnotherProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Task act() => provider.MigrateResourcefullDatabaseAsync();

        // Assert
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("UseSqlServer", exception.Message);
    }
}