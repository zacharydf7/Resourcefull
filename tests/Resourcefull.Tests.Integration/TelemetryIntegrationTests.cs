using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>The SQL Server package's own spans, for migrating and backfilling, and its provider tag on client spans.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class TelemetryIntegrationTests(SqlServerFixture fixture)
{
    private static readonly ActivitySource TestSource = new("Resourcefull.Tests.Integration");

    [SkippableFact]
    public async Task SqlServerOperations_ShouldEmitSpansTaggedWithTheProvider_WhenMigratingBackfillingAndUploading()
    {
        fixture.SkipIfUnavailable();

        // Arrange: spans from this test's trace only, since the listener sees every test's.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ResourcefullTelemetry.ActivitySourceName or "Resourcefull.Tests.Integration",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        string connectionString = fixture.AllocateDatabase("Telemetry");
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(connectionString));
        await using ServiceProvider provider = services.BuildServiceProvider();
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await provider.MigrateResourcefullDatabaseAsync();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
        await container.UploadAsync("r", BinaryData.FromString("x"));
        // Work for the backfill, with counts that differ: two versions without a hash, one with a wrong length.
        ResourceInfo wrong = await container.UploadAsync("s", BinaryData.FromString("y"));
        await using (SqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE [Resources] SET [ContentHash] = NULL; " +
                $"UPDATE [Resources] SET [ContentLength] = 99 WHERE [VersionId] = '{wrong.VersionId}'";
            await command.ExecuteNonQueryAsync();
        }
        using IServiceScope scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ContentHashBackfill>().RunAsync();

        // Assert
        List<Activity> ours = [.. spans.Where(s => s.TraceId == root.TraceId && s.Source.Name == ResourcefullTelemetry.ActivitySourceName)];
        Activity migrate = Assert.Single(ours, s => s.OperationName == "resourcefull migrate");
        Activity backfill = Assert.Single(ours, s => s.OperationName == "resourcefull backfill");
        Activity upload = ours.First(s => s.OperationName == "resourcefull upload");
        Assert.All(new[] { migrate, backfill, upload }, s => Assert.Equal("sqlserver", s.GetTagItem("resourcefull.provider")));
        Assert.Equal(2, backfill.GetTagItem("resourcefull.hashed"));
        Assert.Equal(1, backfill.GetTagItem("resourcefull.mismatches"));
        Assert.Equal(ActivityStatusCode.Unset, migrate.Status);
    }
}
