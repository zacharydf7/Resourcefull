using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.InMemory;
using Resourcefull.Providers;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Models;

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
        Assert.IsType<ResourceSqlRepository>(scope.ServiceProvider.GetRequiredService<ResourceRepository>());
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

    [Fact]
    public void GenerateResourcefullScript_ShouldThrowAndNameUseSqlServer_WhenResourcefullUsesAnotherProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());
        using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => provider.GenerateResourcefullScript());

        // Assert
        Assert.Contains("UseSqlServer", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1files")]
    [InlineData("my-files")]
    [InlineData("files]")]
    [InlineData("files.archive")]
    [InlineData("files\n")]
    public void UseSqlServer_ShouldThrowAndLeaveTheProviderUnconfigured_WhenGivenASchemaThatIsNotARegularIdentifier(string schema)
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Act
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => builder.UseSqlServer("Server=unused;Database=unused", _ => { }, schema));

        // Assert
        Assert.Equal("schema", exception.ParamName);
        Assert.False(builder.ProviderConfigured);
    }

    [Fact]
    public void UseSqlServer_ShouldAcceptUpTo128Characters_WhenGivenALongSchema()
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());
        var other = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Act
        builder.UseSqlServer("Server=unused;Database=unused", _ => { }, new string('s', 128));

        // Assert
        Assert.True(builder.ProviderConfigured);
        Assert.Throws<ArgumentException>(() => other.UseSqlServer("Server=unused;Database=unused", _ => { }, new string('s', 129)));
    }

    [Fact]
    public async Task UseSqlServer_ShouldPutTheTablesAndHistoryInTheSchema_WhenGivenASchema()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer("Server=unused;Database=unused", _ => { }, "files"));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await using ResourceDbContext dbContext =
            await provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();

        // Assert
        Assert.Equal("files", dbContext.Schema);
        Assert.All(dbContext.Model.GetEntityTypes(), entity => Assert.Equal("files", entity.GetSchema()));
        Assert.Equal("files", RelationalOptionsExtension.Extract(dbContext.GetService<IDbContextOptions>()).MigrationsHistoryTableSchema);
    }

    [Fact]
    public async Task UseSqlServer_ShouldBuildAModelForEachSchema_WhenTwoStoresUseDifferentSchemas()
    {
        // Arrange: the contexts share EF's internal services, and with them its cache of models.
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer("Server=unused;Database=unused", _ => { }, "first"));
        var otherServices = new ServiceCollection();
        otherServices.AddResourcefull(options => options.UseSqlServer("Server=unused;Database=unused", _ => { }, "second"));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using ServiceProvider otherProvider = otherServices.BuildServiceProvider();

        // Act
        await using ResourceDbContext first =
            await provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();
        await using ResourceDbContext second =
            await otherProvider.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();

        // Assert
        Assert.Equal("first", first.Model.FindEntityType(typeof(ResourceModel))!.GetSchema());
        Assert.Equal("second", second.Model.FindEntityType(typeof(ResourceModel))!.GetSchema());
    }

    [Fact]
    public async Task UseSqlServer_ShouldLeaveTheTablesUnqualified_WhenGivenNoSchema()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer("Server=unused;Database=unused"));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await using ResourceDbContext dbContext =
            await provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();

        // Assert
        Assert.Null(dbContext.Schema);
        Assert.All(dbContext.Model.GetEntityTypes(), entity => Assert.Null(entity.GetSchema()));
        Assert.Null(RelationalOptionsExtension.Extract(dbContext.GetService<IDbContextOptions>()).MigrationsHistoryTableSchema);
    }

    [Fact]
    public void TableNames_ShouldListEveryTableInTheModel_WhenCalled()
    {
        // Arrange
        using var dbContext = new ResourceDbContext(new DbContextOptionsBuilder<ResourceDbContext>()
            .UseSqlServer("Server=unused;Database=unused").Options);

        // Act
        string?[] tables = [.. dbContext.Model.GetEntityTypes().Select(entity => entity.GetTableName())];

        // Assert: a table missing from the list would stay in the default schema when a schema is given.
        Assert.Equal(tables.Order(), ResourceDbContext.TableNames.Order());
    }

    [Theory]
    [InlineData("SELECT * FROM [Resources] r JOIN [ResourceChunks] c ON c.[ResourceId] = r.[Id]",
        "SELECT * FROM [files].[Resources] r JOIN [files].[ResourceChunks] c ON c.[ResourceId] = r.[Id]")]
    [InlineData("DELETE FROM [ResourceContainers] WHERE [Name] = @name", "DELETE FROM [files].[ResourceContainers] WHERE [Name] = @name")]
    [InlineData("SELECT [Id] FROM [files].[Resources] WITH (INDEX([PK_Resources]))", "SELECT [Id] FROM [files].[Resources] WITH (INDEX([PK_Resources]))")]
    public void QualifyTables_ShouldQualifyEachTableOnce_WhenGivenASchema(string sql, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, ResourceDbContext.QualifyTables(sql, "files"));
    }

    [Fact]
    public void QualifyTables_ShouldKeepTheValuesAsParameters_WhenGivenAnInterpolatedQuery()
    {
        // Arrange
        using var dbContext = new ResourceDbContext(new DbContextOptionsBuilder<ResourceDbContext>()
            .UseSqlServer("Server=unused;Database=unused").Options);
        var withSchema = new DbContextOptionsBuilder<ResourceDbContext>().UseSqlServer("Server=unused;Database=unused");
        ((IDbContextOptionsBuilderInfrastructure)withSchema).AddOrUpdateExtension(new SchemaOptionsExtension("files"));
        using var schemaContext = new ResourceDbContext(withSchema.Options);
        long id = 7;

        // Act
        FormattableString unqualified = dbContext.QualifyTables($"DELETE FROM [Resources] WHERE [Id] = {id}");
        FormattableString qualified = schemaContext.QualifyTables($"DELETE FROM [Resources] WHERE [Id] = {id}");

        // Assert
        Assert.Equal("DELETE FROM [Resources] WHERE [Id] = {0}", unqualified.Format);
        Assert.Equal("DELETE FROM [files].[Resources] WHERE [Id] = {0}", qualified.Format);
        Assert.Equal([7L], qualified.GetArguments());
    }
}