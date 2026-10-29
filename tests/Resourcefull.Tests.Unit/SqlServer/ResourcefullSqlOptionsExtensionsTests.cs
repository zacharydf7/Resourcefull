using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    public void UseSqlServer_ShouldAddResourceRepository_WhenProviderIsConfigured()
    {
        // Arrange
        var services = new ServiceCollection();
        var resourcefullOptionsBuilder = new ResourcefullOptionsBuilder(services);

        // Act
        resourcefullOptionsBuilder.UseSqlServer("");

        // Assert
        Assert.True(resourcefullOptionsBuilder.ProviderConfigured, "Failure: Provider should be configured.");

        ServiceProvider serviceProvider = services.BuildServiceProvider();
        Assert.Contains(services, service => service.ServiceType == typeof(ServiceProviderAccessor));
        Assert.Contains(services, service => service.ServiceType == typeof(DbContextOptions<ResourceDbContext>));
        Assert.Contains(services, service => service.ServiceType == typeof(DbContextOptions));
        Assert.Contains(services, service => service.ServiceType == typeof(IDbContextFactory<ResourceDbContext>));
        Assert.Contains(services, service => service.ServiceType == typeof(ResourceDbContext));
        Assert.Contains(services, service => service.ServiceType == typeof(IResourceRepository));
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