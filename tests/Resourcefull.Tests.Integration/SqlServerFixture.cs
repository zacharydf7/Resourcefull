using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Provisions a real, throwaway SQL Server database for the integration suite and resolves which
/// server to use from the environment:
/// 
/// When no server can be reached, <see cref="IsAvailable"/> is <see langword="false"/> and tests
/// skip rather than fail. Each run gets its own database which is dropped on teardown.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string ConnectionEnvVar = "RESOURCEFULL_TEST_SQLSERVER";
    private const string ReadyTimeoutEnvVar = "RESOURCEFULL_TEST_SQL_READY_TIMEOUT_SECONDS";

    private const string LocalDbServer = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly List<string> _databaseNames = [];
    private string _masterConnectionString = string.Empty;
    private string _runId = string.Empty;

    /// <summary>Whether a SQL Server was reachable and the test database was provisioned.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>Skips the calling test (rather than failing it) when no SQL Server is available.</summary>
    public void SkipIfUnavailable() =>
        Skip.IfNot(IsAvailable, $"No SQL Server available. Set {ConnectionEnvVar} or install LocalDB to run integration tests.");

    /// <summary>Connection string pointing at the provisioned per-run test database.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        string? envConnection = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        bool fromEnvironment = !string.IsNullOrWhiteSpace(envConnection);
        string baseConnection = fromEnvironment ? envConnection! : LocalDbServer;

        _masterConnectionString = new SqlConnectionStringBuilder(baseConnection) { InitialCatalog = "master" }.ConnectionString;

        if (!await WaitForServerAsync(_masterConnectionString, fromEnvironment))
        {
            IsAvailable = false;
            return;
        }

        _runId = Guid.NewGuid().ToString("N");
        ConnectionString = RegisterDatabase($"Resourcefull_IT_{_runId}");

        // Apply the real migration chain so the test database has the production schema.
        await using var dbContext = CreateDbContext(ConnectionString);
        await dbContext.Database.MigrateAsync();

        IsAvailable = true;
    }

    public async Task DisposeAsync()
    {
        if (_databaseNames.Count == 0) return;

        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        foreach (string databaseName in _databaseNames)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $@"
                IF DB_ID(@db) IS NOT NULL
                BEGIN
                    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{databaseName}];
                END";
            command.Parameters.AddWithValue("@db", databaseName);
            await command.ExecuteNonQueryAsync();
        }

        _databaseNames.Clear();
    }

    internal static ResourceDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ResourceDbContext>().UseSqlServer(connectionString).Options);

    /// <summary>Reserves one more throwaway database name, dropped on teardown. Nothing is created.</summary>
    public string AllocateDatabase(string suffix)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("No SQL Server is available; call SkipIfUnavailable() before allocating databases.");
        }

        return RegisterDatabase($"Resourcefull_IT_{_runId}_{suffix}");
    }

    private string RegisterDatabase(string databaseName)
    {
        _databaseNames.Add(databaseName);
        return new SqlConnectionStringBuilder(_masterConnectionString) { InitialCatalog = databaseName }.ConnectionString;
    }

    /// <summary>Builds a fully wired Resourcefull service provider pointed at the test database.</summary>
    public ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseSqlServer(ConnectionString);
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    /// <summary>Opens a raw connection to the test database, e.g. to assert on schema catalog views.</summary>
    public async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> WaitForServerAsync(string masterConnectionString, bool fromEnvironment)
    {
        // A container needs time to warm up, so retry when the server was supplied via the environment.
        // LocalDB is effectively instant, so probe once and fail fast to avoid slow skips on dev machines.
        TimeSpan timeout = fromEnvironment
            ? TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable(ReadyTimeoutEnvVar), out int seconds) ? seconds : 90)
            : TimeSpan.FromSeconds(3);

        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(masterConnectionString);
                await connection.OpenAsync();
                return true;
            }
            catch
            {
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
