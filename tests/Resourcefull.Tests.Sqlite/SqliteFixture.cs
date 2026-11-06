using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Sqlite;

/// <summary>A throwaway SQLite database for the suite, in a file under the system's temporary directory, deleted on teardown.</summary>
public sealed class SqliteFixture : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("resourcefull-it-").FullName;

    /// <summary>The connection string of the run's database.</summary>
    public string ConnectionString => NewConnectionString("resources.db");

    /// <summary>The connection string of a database file of its own, for a test that needs one.</summary>
    public string NewConnectionString(string fileName) =>
        new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, fileName) }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await using ServiceProvider services = BuildServiceProvider();
        await services.MigrateResourcefullDatabaseAsync();
    }

    public ValueTask DisposeAsync()
    {
        // Pooled connections keep the files open, which Windows won't delete.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>Builds a fully wired Resourcefull service provider on <paramref name="connectionString"/>, or the run's database.</summary>
    public ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null, string? connectionString = null)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseSqlite(connectionString ?? ConnectionString);
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }
}

[CollectionDefinition(Name)]
public sealed class SqliteCollection : ICollectionFixture<SqliteFixture>
{
    public const string Name = "Sqlite";
}
