using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

public class ResourcefullDatabaseExtensionsTests
{
    private sealed class RecordingDatabase : ResourceDatabase
    {
        public int Migrations { get; private set; }

        public override Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            Migrations++;
            return Task.CompletedTask;
        }

        public override string GenerateScript() => "the script";
    }

    [Fact]
    public async Task MigrateResourcefullDatabaseAsync_ShouldMigrateTheStoresOwnDatabase_WhenGivenAStoreName()
    {
        // Arrange
        var unnamed = new RecordingDatabase();
        var archive = new RecordingDatabase();
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());
        services.AddKeyedResourcefull("archive", options => options.UseInMemory());
        services.AddSingleton<ResourceDatabase>(unnamed).AddKeyedSingleton<ResourceDatabase>("archive", archive);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        await provider.MigrateResourcefullDatabaseAsync("archive");

        // Assert
        Assert.Equal(1, archive.Migrations);
        Assert.Equal(0, unnamed.Migrations);
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldReturnTheStoresOwnScript_WhenGivenAStoreName()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());
        services.AddKeyedResourcefull("archive", options => options.UseInMemory());
        services.AddKeyedSingleton<ResourceDatabase>("archive", new RecordingDatabase());
        using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        string script = provider.GenerateResourcefullScript("archive");

        // Assert
        Assert.Equal("the script", script);
        Assert.Throws<NotSupportedException>(() => provider.GenerateResourcefullScript());
    }

    [Fact]
    public async Task MigrateResourcefullDatabaseAsync_ShouldDoNothing_WhenTheStoresProviderKeepsNoDatabase()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act & Assert
        await provider.MigrateResourcefullDatabaseAsync();
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldThrowNotSupportedException_WhenTheStoresProviderKeepsNoDatabase()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());
        using ServiceProvider provider = services.BuildServiceProvider();

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => provider.GenerateResourcefullScript());
    }

    [Fact]
    public async Task MigrateResourcefullDatabaseAsync_ShouldThrowAndNameTheStore_WhenNoStoreHasTheName()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseInMemory());
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Task act() => provider.MigrateResourcefullDatabaseAsync("archive");

        // Assert
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("'archive'", exception.Message);
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldThrowAndNameAddResourcefull_WhenResourcefullIsNotAdded()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        // Act
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => provider.GenerateResourcefullScript());

        // Assert
        Assert.Contains("AddResourcefull", exception.Message);
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldThrowArgumentException_WhenGivenABlankStoreName()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();

        // Act & Assert
        Assert.Throws<ArgumentException>("storeName", () => provider.GenerateResourcefullScript(" "));
    }
}
