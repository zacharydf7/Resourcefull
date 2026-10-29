using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>The SQL Server health check against databases in each state it reports: current, behind, ahead and gone.</summary>
[Collection(SqlServerCollection.Name)]
public class SqlServerHealthCheckIntegrationTests(SqlServerFixture fixture)
{
    internal static async Task<HealthReportEntry> CheckAsync(string connectionString, TimeSpan? timeout = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddResourcefull(options => options.UseSqlServer(connectionString));
        services.AddHealthChecks().AddResourcefullSqlServerCheck(timeout: timeout);
        await using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        return report.Entries["resourcefull"];
    }

    /// <summary>A database migrated up to, and including, <paramref name="lastMigration"/>; every migration when null.</summary>
    private async Task<string> NewDatabaseAsync(string suffix, string? lastMigration = null)
    {
        string connectionString = fixture.AllocateDatabase(suffix);
        await using ResourceDbContext context = SqlServerFixture.CreateDbContext(connectionString);
        await context.GetService<IMigrator>().MigrateAsync(lastMigration);
        return connectionString;
    }

    [SkippableFact]
    public async Task AddResourcefullSqlServerCheck_ShouldReportHealthy_WhenTheDatabaseHasEveryMigration()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string connectionString = await NewDatabaseAsync("HealthCurrent");

        // Act
        HealthReportEntry entry = await CheckAsync(connectionString);

        // Assert
        Assert.Equal(HealthStatus.Healthy, entry.Status);
    }

    [SkippableFact]
    public async Task AddResourcefullSqlServerCheck_ShouldReportUnhealthyNamingThem_WhenTheDatabaseIsMissingMigrations()
    {
        fixture.SkipIfUnavailable();

        // Arrange: every migration but the last.
        string[] migrations;
        await using (ResourceDbContext context = SqlServerFixture.CreateDbContext(fixture.ConnectionString))
            migrations = [.. context.Database.GetMigrations()];
        string connectionString = await NewDatabaseAsync("HealthBehind", migrations[^2]);

        // Act
        HealthReportEntry entry = await CheckAsync(connectionString);

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal([migrations[^1]], (string[])entry.Data["pendingMigrations"]);
        Assert.Contains(migrations[^1], entry.Description);
    }

    [SkippableFact]
    public async Task AddResourcefullSqlServerCheck_ShouldReportDegraded_WhenANewerReleaseUpgradedTheDatabase()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a migration this release doesn't know, as a newer release would have applied.
        string connectionString = await NewDatabaseAsync("HealthAhead");
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) " +
                "VALUES ('29990101000000_FromANewerRelease', '10.0.14')";
            await command.ExecuteNonQueryAsync();
        }

        // Act
        HealthReportEntry entry = await CheckAsync(connectionString);

        // Assert
        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Equal(["29990101000000_FromANewerRelease"], (string[])entry.Data["unknownMigrations"]);
    }

    [SkippableFact]
    public async Task AddResourcefullSqlServerCheck_ShouldReportUnhealthy_WhenTheServerCantBeReachedInTime()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a port nothing listens on, and a timeout shorter than the retries behind the connection.
        var unreachable = new SqlConnectionStringBuilder(fixture.ConnectionString) { DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 1 };

        // Act
        HealthReportEntry entry = await CheckAsync(unreachable.ConnectionString, TimeSpan.FromSeconds(3));

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
    }
}
