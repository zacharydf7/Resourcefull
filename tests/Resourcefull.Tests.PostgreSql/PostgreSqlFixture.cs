using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Resourcefull.Tests.PostgreSql;

/// <summary>
/// Provisions a throwaway PostgreSQL database for the suite, on the server <c>RESOURCEFULL_TEST_POSTGRESQL</c> names.
/// Without it, tests skip; a server it names that can't be reached fails the suite. Each run gets its own database,
/// dropped on teardown.
/// </summary>
public class PostgreSqlFixture : IAsyncLifetime
{
    private const string ConnectionEnvVar = "RESOURCEFULL_TEST_POSTGRESQL";

    private readonly List<string> _databaseNames = [];
    private string _serverConnectionString = string.Empty;
    private string _runId = string.Empty;

    /// <summary>Whether a PostgreSQL server was named and the test database was provisioned.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Skips the calling test, rather than failing it, when no PostgreSQL server is named.</summary>
    public void SkipIfUnavailable() =>
        Assert.SkipUnless(IsAvailable, $"No PostgreSQL server named. Set {ConnectionEnvVar} to run the PostgreSQL tests.");

    /// <summary>The connection string of the provisioned per-run test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The schema given to <c>UsePostgreSql</c>; <see langword="null"/> leaves the tables in <c>public</c>.</summary>
    public virtual string? Schema => null;

    public async ValueTask InitializeAsync()
    {
        string? environment = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(environment)) return;

        _serverConnectionString = new NpgsqlConnectionStringBuilder(environment) { Database = "postgres" }.ConnectionString;
        await WaitForServerAsync();
        _runId = Guid.NewGuid().ToString("N");
        ConnectionString = RegisterDatabase($"resourcefull_it_{_runId}");
        await CreateDatabaseAsync(ConnectionString);

        await using ServiceProvider services = BuildServiceProvider();
        await services.MigrateResourcefullDatabaseAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_databaseNames.Count == 0) return;
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        foreach (string databaseName in _databaseNames)
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        _databaseNames.Clear();
    }

    /// <summary>Reserves and creates one more throwaway database, dropped on teardown.</summary>
    public async Task<string> AllocateDatabaseAsync(string suffix)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("No PostgreSQL server is available; call SkipIfUnavailable() before allocating databases.");
        }
        string connectionString = RegisterDatabase($"resourcefull_it_{_runId}_{suffix.ToLowerInvariant()}");
        await CreateDatabaseAsync(connectionString);
        return connectionString;
    }

    /// <summary>Builds a fully wired Resourcefull service provider pointed at the test database.</summary>
    public ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UsePostgreSql(ConnectionString, o => o.Schema = Schema);
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    private string RegisterDatabase(string databaseName)
    {
        _databaseNames.Add(databaseName);
        return new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = databaseName }.ConnectionString;
    }

    private async Task CreateDatabaseAsync(string connectionString)
    {
        string databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        await using var connection = new NpgsqlConnection(_serverConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForServerAsync()
    {
        // A server just started in a Docker container needs time to accept connections.
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_serverConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSql";
}

/// <summary>A test database whose tables are in a schema of their own rather than <c>public</c>.</summary>
public sealed class PostgreSqlSchemaFixture : PostgreSqlFixture
{
    public override string? Schema => "resourcefull";
}

[CollectionDefinition(Name)]
public sealed class PostgreSqlSchemaCollection : ICollectionFixture<PostgreSqlSchemaFixture>
{
    public const string Name = "PostgreSqlSchema";
}
