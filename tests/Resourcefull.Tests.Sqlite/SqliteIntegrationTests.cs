using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resourcefull.Sqlite;

namespace Resourcefull.Tests.Sqlite;

[Collection(SqliteCollection.Name)]
public class SqliteIntegrationTests(SqliteFixture fixture)
{
    [Fact]
    public async Task UploadAsync_ShouldStoreOneVersion_WhenProcessesSharingTheDatabaseCreateTheSameNameAtOnce()
    {
        // Arrange: two service providers on one database file, each with connections of its own, as two processes have.
        await using ServiceProvider first = fixture.BuildServiceProvider();
        await using ServiceProvider second = fixture.BuildServiceProvider();
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient[] containers =
        [
            await first.GetRequiredService<IResourceServiceClient>().CreateContainerAsync(containerName),
            second.GetRequiredService<IResourceServiceClient>().GetContainerClient(containerName)
        ];

        // Act: twenty create-only uploads of one name, half through each.
        Exception?[] failures = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            try
            {
                await containers[i % 2].UploadAsync("a.txt", BinaryData.FromString($"{i}"));
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        })));

        // Assert: one stored, every other refused because the name exists.
        Assert.Single(failures, failure => failure is null);
        Assert.All(failures.Where(failure => failure is not null), failure => Assert.IsType<ResourceConflictException>(failure));
        Assert.Single(await containers[1].GetResourceVersionsAsync("a.txt"));
    }

    [Theory]
    [InlineData("a/", "a0")]
    [InlineData("a\U0010FFFF", "b")]
    [InlineData("\uD7FF", "\uE000")]
    [InlineData("\U0001F600", "\U0001F601")]
    public void AfterPrefix_ShouldGiveTheFirstStringAfterEveryOneThatStartsWithThePrefix_WhenGivenAPrefix(string prefix,
        string expected)
    {
        // Act
        string? end = ResourceSqliteRepository.AfterPrefix(prefix);

        // Assert
        Assert.Equal(expected, end);
    }

    [Fact]
    public void AfterPrefix_ShouldGiveNothing_WhenThePrefixIsOnlyU10FFFF()
    {
        // Act
        string? end = ResourceSqliteRepository.AfterPrefix("\U0010FFFF\U0010FFFF");

        // Assert: no string sorts after every one that starts with it.
        Assert.Null(end);
    }

    [Fact]
    public void AfterPrefix_ShouldGiveNothing_WhenThePrefixHasASurrogateWithoutItsPair()
    {
        // Arrange: built here, since an InlineData argument would turn a lone surrogate into U+FFFD.
        string prefix = "a" + (char)0xD800;

        // Act
        string? end = ResourceSqliteRepository.AfterPrefix(prefix);

        // Assert
        Assert.Null(end);
    }

    [Fact]
    public async Task MigrateResourcefullDatabaseAsync_ShouldTurnOnWriteAheadLogging_WhenGivenANewDatabase()
    {
        // Arrange
        string connectionString = fixture.NewConnectionString($"{Guid.NewGuid():N}.db");
        await using ServiceProvider services = fixture.BuildServiceProvider(connectionString: connectionString);

        // Act
        await services.MigrateResourcefullDatabaseAsync();

        // Assert
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", await command.ExecuteScalarAsync());
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldCreateTheTables_WhenCalled()
    {
        // Arrange
        using ServiceProvider services = fixture.BuildServiceProvider();

        // Act
        string script = services.GenerateResourcefullScript();

        // Assert
        Assert.Contains("CREATE TABLE \"Resources\"", script);
        Assert.Contains("CREATE TABLE \"ResourceChunks\"", script);
    }

    [Theory]
    [InlineData(true, HealthStatus.Healthy)]
    [InlineData(false, HealthStatus.Unhealthy)]
    public async Task AddResourcefullSqliteCheck_ShouldReportWhetherTheDatabaseHasEveryMigration_WhenGivenADatabase(bool migrated,
        HealthStatus expected)
    {
        // Arrange
        string connectionString = fixture.NewConnectionString($"{Guid.NewGuid():N}.db");
        var collection = new ServiceCollection();
        collection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        collection.AddResourcefull(options => options.UseSqlite(connectionString));
        collection.AddHealthChecks().AddResourcefullSqliteCheck();
        await using ServiceProvider services = collection.BuildServiceProvider();
        if (migrated) await services.MigrateResourcefullDatabaseAsync();

        // Act
        HealthReport report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        // Assert
        HealthReportEntry entry = report.Entries["resourcefull"];
        Assert.Equal(expected, entry.Status);
        Assert.Equal("sqlite", entry.Data["provider"]);
    }
}
