using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Holds the shipped scripts to the migrations they are generated from (no server needed), and proves they provision
/// a database the library can use with nothing left for the migration tooling to do.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class ProvisioningScriptTests(SqlServerFixture fixture)
{
    private const string PhantomMigrationId = "20240524133017_InitialCreate";

    public static TheoryData<string> ExpectedScripts()
    {
        var scripts = new TheoryData<string>();
        foreach (ProvisioningScript script in ProvisioningScripts.Expected())
        {
            scripts.Add(script.RelativePath);
        }

        return scripts;
    }

    [Theory]
    [MemberData(nameof(ExpectedScripts))]
    public void CommittedScript_ShouldBeThePreambleFollowedByTheGeneratedBody_WhenRegeneratedFromTheCurrentModel(string relativePath)
    {
        // Arrange
        ProvisioningScript script = ProvisioningScripts.Expected().Single(s => s.RelativePath == relativePath);
        string expected = ProvisioningScripts.Normalize(script.Preamble + ProvisioningScripts.GenerateBody(script.FromMigration, script.ToMigration));
        string path = Path.Combine(ProvisioningScripts.ScriptsDirectory, script.RelativePath);
        Assert.True(File.Exists(path), $"{script.RelativePath} is missing from {ProvisioningScripts.ScriptsRelativeDirectory}. Generate it with:{Environment.NewLine}  {script.Command}{Environment.NewLine}then put the preamble back at the top.");

        // Act
        string actual = ProvisioningScripts.Normalize(File.ReadAllText(path));

        // Assert
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Assert.Fail(
                $"{script.RelativePath} no longer matches the migrations in Resourcefull.SqlServer.{Environment.NewLine}" +
                $"{ProvisioningScripts.DescribeFirstDifference(expected, actual)}{Environment.NewLine}" +
                $"Regenerate it with:{Environment.NewLine}  {script.Command}{Environment.NewLine}" +
                "then put the preamble back at the top (see docs-v2-reference.md, Regenerating the scripts).");
        }
    }

    [Fact]
    public void ScriptsDirectory_ShouldContainExactlyTheExpectedScripts_WhenComparedWithTheMigrations()
    {
        // Arrange
        var expected = ProvisioningScripts.Expected()
            .Select(script => script.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Act
        var committed = Directory.GetFiles(ProvisioningScripts.ScriptsDirectory, "*.sql", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ProvisioningScripts.ScriptsDirectory, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Assert
        List<string> unexpected = [.. committed.Except(expected).Order(StringComparer.OrdinalIgnoreCase)];
        List<string> missing = [.. expected.Except(committed).Order(StringComparer.OrdinalIgnoreCase)];
        if (unexpected.Count > 0 || missing.Count > 0)
        {
            Assert.Fail(
                $"{ProvisioningScripts.ScriptsRelativeDirectory} does not hold one script per shipped migration range.{Environment.NewLine}" +
                $"Missing: {(missing.Count == 0 ? "(none)" : string.Join(", ", missing))}{Environment.NewLine}" +
                $"Unexpected: {(unexpected.Count == 0 ? "(none)" : string.Join(", ", unexpected))}{Environment.NewLine}" +
                "Every migration after the first ships an upgrade script, and the package project must list each file as Content.");
        }
    }

    [SkippableFact]
    public async Task FreshInstallScript_ShouldProduceAWorkingDatabase_WhenRunAgainstAnEmptyServer()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        ProvisioningScript script = ProvisioningScripts.Expected().Single(s => s.FromMigration is null);
        string connectionString = fixture.AllocateDatabase("Provisioned");

        // Act
        await ProvisioningScripts.ApplyAsync(ProvisioningScripts.ReadCommitted(script), connectionString);

        // Assert
        await AssertCurrentSchemaAsync(connectionString);
    }

    [SkippableFact]
    public async Task FreshInstallScript_ShouldChangeNothing_WhenRunASecondTime()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        ProvisioningScript script = ProvisioningScripts.Expected().Single(s => s.FromMigration is null);
        string connectionString = fixture.AllocateDatabase("Rerun");
        string sql = ProvisioningScripts.ReadCommitted(script);
        await ProvisioningScripts.ApplyAsync(sql, connectionString);
        List<string> historyAfterFirstRun = await ReadMigrationHistoryAsync(connectionString);
        int objectsAfterFirstRun = await CountUserObjectsAsync(connectionString);

        // Act
        await ProvisioningScripts.ApplyAsync(sql, connectionString);

        // Assert
        Assert.Equal(historyAfterFirstRun, await ReadMigrationHistoryAsync(connectionString));
        Assert.Equal(objectsAfterFirstRun, await CountUserObjectsAsync(connectionString));
        await AssertCurrentSchemaAsync(connectionString);
    }

    [SkippableFact]
    public async Task UpgradeScripts_ShouldBringAFirstVersionDatabaseToTheCurrentSchema_WhenRunInOrder()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        IReadOnlyList<string> ids = ProvisioningScripts.MigrationIds;
        string connectionString = fixture.AllocateDatabase("Upgraded");
        await using (ResourceDbContext context = SqlServerFixture.CreateDbContext(connectionString))
        {
            await context.Database.MigrateAsync(ids[0]);
        }

        // Act
        foreach (ProvisioningScript upgrade in ProvisioningScripts.Expected().Where(s => s.FromMigration is not null))
        {
            await ProvisioningScripts.ApplyAsync(ProvisioningScripts.ReadCommitted(upgrade), connectionString);
        }

        // Assert
        await AssertCurrentSchemaAsync(connectionString);
    }

    [SkippableFact]
    public async Task UpgradeScripts_ShouldCarryExistingResourcesAcross_WhenAFirstVersionDatabaseHoldsData()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        IReadOnlyList<string> ids = ProvisioningScripts.MigrationIds;
        string connectionString = fixture.AllocateDatabase("UpgradedWithData");
        await using (ResourceDbContext context = SqlServerFixture.CreateDbContext(connectionString))
        {
            await context.Database.MigrateAsync(ids[0]);
        }

        const string containerName = "legacy";
        const string resourceName = "doc.txt";
        byte[] payload = Encoding.UTF8.GetBytes("stored before versioning existed");
        await InsertFirstVersionResourceAsync(connectionString, containerName, resourceName, payload);

        // Act
        foreach (ProvisioningScript upgrade in ProvisioningScripts.Expected().Where(s => s.FromMigration is not null))
        {
            await ProvisioningScripts.ApplyAsync(ProvisioningScripts.ReadCommitted(upgrade), connectionString);
        }

        // Assert
        string? versionId = (string?)await ScalarAsync(connectionString, "SELECT [VersionId] FROM [Resources] WHERE [ContainerName] = 'legacy' AND [Name] = 'doc.txt'");
        Assert.False(string.IsNullOrEmpty(versionId), "AddVersioning did not assign a VersionId to the existing resource.");
        Assert.Equal(true, await ScalarAsync(connectionString, "SELECT [IsLatest] FROM [Resources] WHERE [ContainerName] = 'legacy' AND [Name] = 'doc.txt'"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM [ResourceChunks] c INNER JOIN [Resources] r ON r.[Id] = c.[ResourceId] WHERE r.[Name] = 'doc.txt'"));

        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(connectionString));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ResourceDownloadResult? download = await scope.ServiceProvider.GetRequiredService<IResourceServiceClient>()
            .GetResourceContainerClient(containerName)
            .DownloadStreamingAsync(resourceName);
        Assert.NotNull(download);
        Assert.NotNull(download.Stream);
        await using Stream stored = download.Stream;
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());

        await AssertCurrentSchemaAsync(connectionString);
    }

    [SkippableFact]
    public async Task NameLimitsUpgradeScript_ShouldChangeNothing_WhenAStoredNameIsLongerThanTheNewLimit()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a database one migration behind, holding a container name the new 128-character limit cannot fit.
        const string migrationId = "20261028213132_NameLimitsAndCaseSensitiveNames";
        ProvisioningScript upgrade = ProvisioningScripts.Expected().Single(s => s.ToMigration == migrationId);
        string connectionString = fixture.AllocateDatabase("TooLongName");
        await using (ResourceDbContext context = SqlServerFixture.CreateDbContext(connectionString))
        {
            await context.Database.MigrateAsync(upgrade.FromMigration);
        }
        await ScalarAsync(connectionString, $"INSERT INTO [ResourceContainers] ([Name], [Uri], [Metadata]) VALUES ('{new string('c', 129)}', NULL, '{{}}')");

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProvisioningScripts.ApplyAsync(ProvisioningScripts.ReadCommitted(upgrade), connectionString));

        // Assert
        Assert.Equal(0, await ScalarAsync(connectionString, $"SELECT COUNT(*) FROM [__EFMigrationsHistory] WHERE [MigrationId] = '{migrationId}'"));
        Assert.Equal(450, await ScalarAsync(connectionString, "SELECT COLUMNPROPERTY(OBJECT_ID('Resources'), 'Name', 'CharMaxLen')"));
        Assert.Equal(1, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.foreign_keys WHERE [name] = 'FK_Resources_ResourceContainers_ContainerName'"));
    }

    /// <summary>Rows in the shape the first migration created them.</summary>
    private static async Task InsertFirstVersionResourceAsync(string connectionString, string containerName, string resourceName, byte[] payload)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [ResourceContainers] ([Name], [Uri], [Metadata]) VALUES (@container, NULL, '{}');
            INSERT INTO [Resources] ([ContainerName], [Name], [ContentType], [ContentLength], [Uri], [Metadata])
            VALUES (@container, @resource, 'text/plain', @length, NULL, '{}');
            INSERT INTO [ResourceChunks] ([ResourceContainerName], [ResourceName], [Index], [Data]) VALUES (@container, @resource, 0, @data);
            """;
        command.Parameters.AddWithValue("@container", containerName);
        command.Parameters.AddWithValue("@resource", resourceName);
        command.Parameters.AddWithValue("@length", (long)payload.Length);
        command.Parameters.AddWithValue("@data", payload);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertCurrentSchemaAsync(string connectionString)
    {
        await using (ResourceDbContext context = SqlServerFixture.CreateDbContext(connectionString))
        {
            IEnumerable<string> pending = await context.Database.GetPendingMigrationsAsync();
            Assert.True(!pending.Any(), $"The migration tooling still has work to do: {string.Join(", ", pending)}.");
            Assert.Equal(context.Database.GetMigrations().Order(StringComparer.Ordinal), (await context.Database.GetAppliedMigrationsAsync()).Order(StringComparer.Ordinal));
        }

        List<string> history = await ReadMigrationHistoryAsync(connectionString);
        Assert.DoesNotContain(PhantomMigrationId, history);
        Assert.Equal(true, await ScalarAsync(connectionString, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()"));
        Assert.Equal("UX_Resources_Container_Name_Version", await ScalarAsync(connectionString, "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('Resources') AND type_desc = 'CLUSTERED'"));

        await AssertLibraryRoundTripAsync(connectionString);
    }

    private static async Task AssertLibraryRoundTripAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(connectionString));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();

        string containerName = $"provisioning-{Guid.NewGuid():N}";
        const string resourceName = "smoke.txt";
        byte[] payload = Encoding.UTF8.GetBytes("written through the library into a database provisioned by script");

        await client.CreateResourceContainerIfNotExistsAsync(containerName, new Dictionary<string, string>());
        IResourceContainerClient container = client.GetResourceContainerClient(containerName);
        await using (var upload = new MemoryStream(payload))
        {
            await container.UploadAsync(resourceName, upload, new ResourceUploadOptions
            {
                Headers = new("text/plain", payload.Length),
                Metadata = new Dictionary<string, string>()
            });
        }

        ResourceDownloadResult? download = await container.DownloadStreamingAsync(resourceName);
        Assert.NotNull(download);
        Assert.NotNull(download.Stream);
        await using Stream stored = download.Stream;
        using var buffer = new MemoryStream();
        await stored.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    private static async Task<List<string>> ReadMigrationHistoryAsync(string connectionString)
    {
        var ids = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT [MigrationId] FROM [__EFMigrationsHistory] ORDER BY [MigrationId]";
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<int> CountUserObjectsAsync(string connectionString) =>
        (int)(await ScalarAsync(connectionString, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0") ?? 0);

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
