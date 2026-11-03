using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>Two SQL Server stores in one application, each with a database of its own.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class KeyedStoreIntegrationTests(SqlServerFixture fixture)
{
    private static readonly ActivitySource TestSource = new("Resourcefull.Tests.Integration.KeyedStores");

    [SkippableFact]
    public async Task AddKeyedResourcefull_ShouldMigrateAndUseEachStoresOwnDatabase_WhenTwoStoresUseSqlServer()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ResourcefullTelemetry.ActivitySourceName or "Resourcefull.Tests.Integration.KeyedStores",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddResourcefull(options => options.UseSqlServer(fixture.AllocateDatabase("KeyedHot")));
        services.AddKeyedResourcefull("archive", options => options.UseSqlServer(fixture.AllocateDatabase("KeyedArchive")));
        services.AddHealthChecks().AddResourcefullSqlServerCheck("hot").AddResourcefullSqlServerCheck("archive", storeName: "archive");
        await using ServiceProvider provider = services.BuildServiceProvider();
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await provider.MigrateResourcefullDatabaseAsync();
        await provider.MigrateResourcefullDatabaseAsync("archive");
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var archive = scope.ServiceProvider.GetRequiredKeyedService<IResourceServiceClient>("archive");
        await (await archive.CreateContainerAsync("c")).UploadAsync("a.txt", BinaryData.FromString("old"));

        // Assert
        Assert.True(await archive.ContainerExistsAsync("c"));
        Assert.False(await scope.ServiceProvider.GetRequiredService<IResourceServiceClient>().ContainerExistsAsync("c"));
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredKeyedService<ContentHashBackfill>("archive").RunAsync()).Hashed);
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Healthy, report.Entries["hot"].Status);
        Assert.Equal(HealthStatus.Healthy, report.Entries["archive"].Status);
        Activity[] migrations = [.. spans.Where(s => s.TraceId == root.TraceId && s.OperationName == "resourcefull migrate")];
        Assert.Equal([null, "archive"], migrations.Select(s => s.GetTagItem("resourcefull.store")));
    }

    [Fact]
    public void GenerateResourcefullScript_ShouldUseTheNamedStoresSchema_WhenGivenAStoreName()
    {
        // Arrange: generating a script never connects.
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer("Server=(non-existent);Database=Hot;Encrypt=False"));
        services.AddKeyedResourcefull("archive", options =>
            options.UseSqlServer("Server=(non-existent);Database=Archive;Encrypt=False", _ => { }, schema: "archive"));
        using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        string hot = provider.GenerateResourcefullScript();
        string archive = provider.GenerateResourcefullScript("archive");

        // Assert
        Assert.DoesNotContain("[archive].", hot);
        Assert.Contains("CREATE TABLE [archive].[Resources]", archive);
    }
}
