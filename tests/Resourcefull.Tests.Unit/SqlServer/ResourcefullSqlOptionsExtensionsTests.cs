using System.Data.Common;
using Microsoft.EntityFrameworkCore;
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
    public async Task UseSqlServer_ShouldApplyTheCallersOptionsAndKeepCountingRetries_WhenGivenOptionsToChange()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
            options.UseSqlServer("Server=unused;Database=unused", sql => sql.CommandTimeout(77)));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await using ResourceDbContext dbContext =
            await provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();
        await using DbCommand pageRead = ResourceSqlRepository.NewCommand(dbContext);

        // Assert: download page reads, which make their own command, get the timeout too.
        Assert.Equal(77, dbContext.Database.GetCommandTimeout());
        Assert.Equal(77, pageRead.CommandTimeout);
        Assert.IsType<RetryCountingExecutionStrategy>(dbContext.Database.CreateExecutionStrategy());
    }

    [Fact]
    public void UseSqlServer_ShouldThrowAndLeaveTheProviderUnconfigured_WhenGivenNullOptions()
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => builder.UseSqlServer("Server=unused;Database=unused", null!));
        Assert.False(builder.ProviderConfigured);
    }

    [Fact]
    public async Task UseSqlServer_ShouldUseTheCallersRetryPolicy_WhenTheirOptionsReplaceIt()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer("Server=unused;Database=unused",
            sql => sql.ExecutionStrategy(dependencies => new SqlServerRetryingExecutionStrategy(dependencies, 1))));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await using ResourceDbContext dbContext =
            await provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();

        // Assert
        Assert.IsType<SqlServerRetryingExecutionStrategy>(dbContext.Database.CreateExecutionStrategy());
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