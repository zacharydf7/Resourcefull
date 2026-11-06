using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>Resourcefull's tables in a schema of their own, given to <c>UseSqlServer</c>.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class CustomSchemaIntegrationTests(SqlServerFixture fixture)
{
    private static readonly ActivitySource TestSource = new("Resourcefull.Tests.Integration.Schema");

    private static readonly string[] Tables = ["__EFMigrationsHistory", "ResourceChunks", "ResourceContainers", "ResourceMetadata", "Resources"];

    [Fact]
    public async Task MigrateResourcefullDatabaseAsync_ShouldCreateTheSchemaWithTheTablesAndHistory_WhenGivenASchema()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ResourcefullTelemetry.ActivitySourceName or "Resourcefull.Tests.Integration.Schema",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        string connectionString = fixture.AllocateDatabase("Schema");
        await using ServiceProvider services = BuildServices(connectionString, "files");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await services.MigrateResourcefullDatabaseAsync();

        // Assert
        Assert.Equal(Tables, await TablesAsync(connectionString, "files"));
        Assert.Empty(await TablesAsync(connectionString, "dbo"));
        Activity migrate = Assert.Single(spans, s => s.TraceId == root.TraceId && s.OperationName == "resourcefull migrate");
        Assert.Equal("files", migrate.GetTagItem("resourcefull.schema"));
    }

    [Fact]
    public async Task UseSqlServer_ShouldKeepEachStoresResourcesApart_WhenTwoSchemasShareADatabase()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string connectionString = fixture.AllocateDatabase("TwoSchemas");
        await using ServiceProvider unqualified = BuildServices(connectionString, schema: null);
        await using ServiceProvider files = BuildServices(connectionString, "files");
        await unqualified.MigrateResourcefullDatabaseAsync();
        await files.MigrateResourcefullDatabaseAsync();

        // Act
        await UploadAsync(unqualified, "in dbo");
        await UploadAsync(files, "in files");

        // Assert
        Assert.Equal("in dbo", await DownloadAsync(unqualified));
        Assert.Equal("in files", await DownloadAsync(files));
        Assert.Equal(Tables, await TablesAsync(connectionString, "dbo"));
        Assert.Equal(Tables, await TablesAsync(connectionString, "files"));
        Assert.Equal(await ScalarAsync(connectionString, "SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory]"),
            await ScalarAsync(connectionString, "SELECT COUNT(*) FROM [files].[__EFMigrationsHistory]"));
    }

    [Fact]
    public async Task GenerateResourcefullScript_ShouldProvisionTheSchema_WhenRunTwiceOnANewDatabase()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string connectionString = fixture.AllocateDatabase("SchemaScript");
        await using ServiceProvider services = BuildServices(connectionString, "files");
        string script = services.GenerateResourcefullScript();

        // Act
        await ProvisioningScripts.ApplyAsync(script, connectionString);
        await ProvisioningScripts.ApplyAsync(script, connectionString);

        // Assert
        Assert.Equal(Tables, await TablesAsync(connectionString, "files"));
        Assert.Empty(await TablesAsync(connectionString, "dbo"));
        await UploadAsync(services, "scripted");
        Assert.Equal("scripted", await DownloadAsync(services));
    }

    [Fact]
    public async Task UseSqlServer_ShouldFindEveryResourceUpToDate_WhenTheTablesWereMovedIntoTheSchemaAsDocumented()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a database provisioned without a schema, with a file in it.
        string connectionString = fixture.AllocateDatabase("MovedToSchema");
        await using (ServiceProvider unqualified = BuildServices(connectionString, schema: null))
        {
            await unqualified.MigrateResourcefullDatabaseAsync();
            await UploadAsync(unqualified, "moved");
        }

        // Act: the steps in docs/providers/sql-server.md, "Moving existing tables into a schema".
        await ProvisioningScripts.ApplyAsync("""
            CREATE SCHEMA [files];
            GO
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            ALTER SCHEMA [files] TRANSFER [dbo].[ResourceContainers];
            ALTER SCHEMA [files] TRANSFER [dbo].[Resources];
            ALTER SCHEMA [files] TRANSFER [dbo].[ResourceChunks];
            CREATE TABLE [files].[__EFMigrationsHistory] (
                [MigrationId] nvarchar(150) NOT NULL CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY,
                [ProductVersion] nvarchar(32) NOT NULL);
            INSERT INTO [files].[__EFMigrationsHistory] SELECT [MigrationId], [ProductVersion] FROM [dbo].[__EFMigrationsHistory];
            COMMIT;
            """, connectionString);
        await using ServiceProvider files = BuildServices(connectionString, "files");

        // Assert
        Assert.Equal("moved", await DownloadAsync(files));
        await files.GetRequiredService<IResourceServiceClient>().GetContainerClient("c").UploadAsync("a.txt",
            BinaryData.FromString("after the move"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        Assert.Equal("after the move", await DownloadAsync(files));
        await using ResourceDbContext dbContext =
            await files.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();
        Assert.Empty(await dbContext.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldMatchThePackagedFreshInstallScript_WhenGivenNoSchema()
    {
        // Arrange: generating a script never connects.
        using ServiceProvider services = BuildServices("Server=(non-existent);Database=ResourceDB;Encrypt=False", schema: null);
        string packaged = ProvisioningScripts.Normalize(ProvisioningScripts.ReadCommitted(ProvisioningScripts.Expected()[0]));

        // Act
        string generated = ProvisioningScripts.Normalize(services.GenerateResourcefullScript());

        // Assert
        Assert.True(packaged == generated, ProvisioningScripts.DescribeFirstDifference(packaged, generated));
    }

    private static ServiceProvider BuildServices(string connectionString, string? schema)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(connectionString, o => o.Schema = schema));
        return services.BuildServiceProvider();
    }

    private static async Task UploadAsync(IServiceProvider services, string content)
    {
        IResourceContainerClient container = await services.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a.txt", BinaryData.FromString(content));
    }

    private static async Task<string> DownloadAsync(IServiceProvider services)
    {
        ResourceDownloadResult? download = await services.GetRequiredService<IResourceServiceClient>()
            .GetContainerClient("c").DownloadStreamingAsync("a.txt");
        await using Stream stream = Assert.IsAssignableFrom<Stream>(download?.Stream);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<List<string>> TablesAsync(string connectionString, string schema)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT [name] FROM sys.tables WHERE SCHEMA_NAME([schema_id]) = @schema ORDER BY [name]";
        command.Parameters.AddWithValue("@schema", schema);
        var tables = new List<string>();
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
