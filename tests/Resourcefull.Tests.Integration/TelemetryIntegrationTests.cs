using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
        Assert.Equal(1, upload.GetTagItem("resourcefull.batches"));
        Assert.Equal(2, backfill.GetTagItem("resourcefull.hashed"));
        Assert.Equal(1, backfill.GetTagItem("resourcefull.mismatches"));
        Assert.Equal(ActivityStatusCode.Unset, migrate.Status);
    }

    [SkippableFact]
    public async Task AddResourcefullSqlServerCheck_ShouldRecordItsSpanWithPendingMigrationsOrItsFailure_WhenCalled()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a database missing its last migration, and a server that accepts the connection but never answers, so
        // the check times out.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ResourcefullTelemetry.ActivitySourceName or "Resourcefull.Tests.Integration",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        string behind = fixture.AllocateDatabase("TelemetryHealth");
        await using (ResourceDbContext context = SqlServerFixture.CreateDbContext(behind))
        {
            string[] migrations = [.. context.Database.GetMigrations()];
            await context.GetService<IMigrator>().MigrateAsync(migrations[^2]);
        }
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        string unresponsive = new SqlConnectionStringBuilder(fixture.ConnectionString)
            { DataSource = $"tcp:127.0.0.1,{((IPEndPoint)silent.LocalEndpoint).Port}", ConnectTimeout = 30 }.ConnectionString;
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await SqlServerHealthCheckIntegrationTests.CheckAsync(behind);
        await SqlServerHealthCheckIntegrationTests.CheckAsync(unresponsive, TimeSpan.FromSeconds(1));

        // Assert
        List<Activity> checks = [.. spans.Where(s => s.TraceId == root.TraceId && s.OperationName == "resourcefull health_check")];
        Assert.Equal(2, checks.Count);
        Assert.All(checks, s => Assert.Equal("sqlserver", s.GetTagItem("resourcefull.provider")));
        Assert.Contains(checks, s => Equals(s.GetTagItem("resourcefull.pending_migrations"), 1) && s.Status == ActivityStatusCode.Unset);
        Assert.Contains(checks, s => s.Status == ActivityStatusCode.Error && s.GetTagItem("error.type") is not null);
    }
}
