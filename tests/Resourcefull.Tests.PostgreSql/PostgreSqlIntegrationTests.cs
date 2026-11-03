using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Resourcefull.PostgreSql;
using Resourcefull.Providers;

namespace Resourcefull.Tests.PostgreSql;

/// <summary>What only PostgreSQL does: schemas, provisioning, its health check and its telemetry.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlIntegrationTests(PostgreSqlFixture fixture)
{
    private static readonly ActivitySource TestSource = new("Resourcefull.Tests.PostgreSql");

    private static readonly string[] Tables = ["ResourceChunks", "ResourceContainers", "Resources", "__EFMigrationsHistory"];

    [SkippableFact]
    public async Task MigrateResourcefullDatabaseAsync_ShouldPutTheTablesAndHistoryInTheSchema_WhenGivenASchema()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string connectionString = await fixture.AllocateDatabaseAsync("schema");
        await using ServiceProvider services = BuildServices(connectionString, "files");

        // Act
        await services.MigrateResourcefullDatabaseAsync();

        // Assert
        Assert.Equal(Tables, await TablesAsync(connectionString, "files"));
        Assert.Empty(await TablesAsync(connectionString, "public"));
    }

    [SkippableFact]
    public async Task GenerateResourcefullScript_ShouldProvisionTheSchema_WhenRunTwiceOnANewDatabase()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string connectionString = await fixture.AllocateDatabaseAsync("script");
        await using ServiceProvider services = BuildServices(connectionString, "files");
        string script = services.GenerateResourcefullScript();

        // Act
        await ExecuteAsync(connectionString, script);
        await ExecuteAsync(connectionString, script);

        // Assert
        Assert.Equal(Tables, await TablesAsync(connectionString, "files"));
        IResourceContainerClient container = await services.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a.txt", BinaryData.FromString("scripted"));
        Assert.Equal("scripted", (await container.DownloadContentAsync("a.txt"))!.Content.ToString());
    }

    [SkippableFact]
    public async Task AddKeyedResourcefull_ShouldKeepEachStoresResourcesApart_WhenTwoStoresShareADatabaseInSchemas()
    {
        fixture.SkipIfUnavailable();

        // Arrange: two stores in one database, each in a schema of its own.
        string connectionString = await fixture.AllocateDatabaseAsync("stores");
        var collection = new ServiceCollection();
        collection.AddResourcefull(options => options.UsePostgreSql(connectionString, _ => { }, "hot"));
        collection.AddKeyedResourcefull("archive", options => options.UsePostgreSql(connectionString, _ => { }, "archive"));
        await using ServiceProvider services = collection.BuildServiceProvider();
        await services.MigrateResourcefullDatabaseAsync();
        await services.MigrateResourcefullDatabaseAsync("archive");

        // Act
        await (await services.GetRequiredKeyedService<IResourceServiceClient>("archive").CreateContainerAsync("c"))
            .UploadAsync("a.txt", BinaryData.FromString("old"));

        // Assert
        Assert.True(await services.GetRequiredKeyedService<IResourceServiceClient>("archive").ContainerExistsAsync("c"));
        Assert.False(await services.GetRequiredService<IResourceServiceClient>().ContainerExistsAsync("c"));
        Assert.Equal(Tables, await TablesAsync(connectionString, "hot"));
        Assert.Equal(Tables, await TablesAsync(connectionString, "archive"));
    }

    [SkippableFact]
    public async Task AddResourcefullPostgreSqlCheck_ShouldReportHealthy_WhenTheDatabaseHasEveryMigration()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var collection = new ServiceCollection();
        collection.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        collection.AddResourcefull(options => options.UsePostgreSql(fixture.ConnectionString));
        collection.AddHealthChecks().AddResourcefullPostgreSqlCheck();
        await using ServiceProvider services = collection.BuildServiceProvider();

        // Act
        HealthReport report = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        // Assert
        HealthReportEntry entry = report.Entries["resourcefull"];
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal("postgresql", entry.Data["provider"]);
    }

    [SkippableFact]
    public async Task PostgreSqlOperations_ShouldEmitSpansTaggedWithTheProvider_WhenMigratingAndUploading()
    {
        fixture.SkipIfUnavailable();

        // Arrange: spans from this test's trace only, since the listener sees every test's.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ResourcefullTelemetry.ActivitySourceName or "Resourcefull.Tests.PostgreSql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        await using ServiceProvider services = BuildServices(await fixture.AllocateDatabaseAsync("telemetry"), "files");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await services.MigrateResourcefullDatabaseAsync();
        await (await services.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c"))
            .UploadAsync("a.txt", BinaryData.FromString("x"));

        // Assert
        List<Activity> ours = [.. spans.Where(s => s.TraceId == root.TraceId && s.Source.Name == ResourcefullTelemetry.ActivitySourceName)];
        Activity migrate = Assert.Single(ours, s => s.OperationName == "resourcefull migrate");
        Activity upload = ours.First(s => s.OperationName == "resourcefull upload");
        Assert.All(new[] { migrate, upload }, s => Assert.Equal("postgresql", s.GetTagItem("resourcefull.provider")));
        Assert.Equal("files", migrate.GetTagItem("resourcefull.schema"));
    }

    [SkippableFact]
    public async Task Writes_ShouldThrowArgumentExceptionAndStoreNothing_WhenAValueToStoreHasU0000()
    {
        fixture.SkipIfUnavailable();

        // Arrange: an older release's export can name a resource with U+0000, which PostgreSQL's text can't hold.
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync($"c-{Guid.NewGuid():N}");
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        await container.UploadAsync("a", BinaryData.FromString("a"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => repository.ImportVersionAsync(
            new NewResourceVersion(new ResourceLocation(container.Name, "nul\0name"), "v1"), new MemoryStream([1])));
        await Assert.ThrowsAsync<ArgumentException>(() => container.UploadAsync("b", BinaryData.FromString("b"),
            new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain\0", null) }));
        await Assert.ThrowsAsync<ArgumentException>(() => container.SetContentTypeAsync("a", null, "text/plain\0"));
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(() => container.CopyResourceAsync("a", container.Name + "\0", "a"));
        Assert.Equal(["a"], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
        Assert.Null((await container.GetResourceInfoAsync("a"))!.ContentType);
    }

    [Fact]
    public void UsePostgreSql_ShouldRegisterTheStoresDatabase_WhenTheStoreHasAName()
    {
        // Arrange
        var collection = new ServiceCollection();
        collection.AddKeyedResourcefull("archive", options => options.UsePostgreSql("Host=unused"));
        using ServiceProvider services = collection.BuildServiceProvider();

        // Act
        ResourceDatabase database = services.GetRequiredKeyedService<ResourceDatabase>("archive");

        // Assert
        Assert.IsType<PostgreSqlDatabase>(database);
        Assert.Null(services.GetService<ResourceDatabase>());
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldQualifyTheTablesWithTheSchema_WhenGivenASchema()
    {
        // Arrange: generating a script never connects.
        using ServiceProvider services = BuildServices("Host=unused", "files");

        // Act
        string script = services.GenerateResourcefullScript();

        // Assert
        Assert.Contains("CREATE TABLE files.\"Resources\"", script);
        Assert.DoesNotContain("CREATE TABLE \"Resources\"", script);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1files")]
    [InlineData("my-files")]
    [InlineData("files\"")]
    [InlineData("files\n")]
    public void UsePostgreSql_ShouldThrowAndLeaveTheProviderUnconfigured_WhenGivenASchemaThatIsNotARegularIdentifier(string schema)
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Act
        ArgumentException exception = Assert.Throws<ArgumentException>(() => builder.UsePostgreSql("Host=unused", _ => { }, schema));

        // Assert
        Assert.Equal("schema", exception.ParamName);
        Assert.False(builder.ProviderConfigured);
    }

    [Fact]
    public void UsePostgreSql_ShouldAcceptUpTo63Characters_WhenGivenALongSchema()
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());
        var other = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Act
        builder.UsePostgreSql("Host=unused", _ => { }, new string('s', 63));

        // Assert
        Assert.True(builder.ProviderConfigured);
        Assert.Throws<ArgumentException>(() => other.UsePostgreSql("Host=unused", _ => { }, new string('s', 64)));
    }

    private static ServiceProvider BuildServices(string connectionString, string? schema)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UsePostgreSql(connectionString, _ => { }, schema));
        return services.BuildServiceProvider();
    }

    private static async Task<List<string>> TablesAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema ORDER BY table_name COLLATE \"C\"";
        command.Parameters.AddWithValue("schema", schema);
        var tables = new List<string>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
