using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;
using Resourcefull.Tests.Integration;
using Xunit.Abstractions;

namespace Resourcefull.Tests.Performance;

/// <summary>
/// What operations cost at a scale the performance suite doesn't reach: a file of gibibytes, and a container of a
/// million names. They take many minutes, so they run only when <c>RESOURCEFULL_SCALE</c> is <c>true</c>, as the Scale
/// workflow sets it, and each is measured once and reported rather than held to a baseline.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ScaleTests(SqlServerFixture fixture, ITestOutputHelper output) : IDisposable
{
    private const long Gibibyte = 1L << 30;
    private const int RowsPerSeedBatch = 100_000;

    private readonly SqlActivity _activity = new();
    private ServiceProvider? _provider;
    private string _connectionString = string.Empty;

    private static long FileLength => Setting("RESOURCEFULL_SCALE_GIB", 1, 1, 4) * Gibibyte;

    // Up to seven digits, the width of the seeded names.
    private static int Rows => (int)Setting("RESOURCEFULL_SCALE_ROWS", 1_000_000, 1, 9_999_999);

    /// <summary>Every run's measurements, uploaded by the Scale workflow.</summary>
    public static string ResultsPath => Path.Combine(AppContext.BaseDirectory, "scale-results.json");

    [SkippableFact]
    public async Task LargeFile_ShouldBeMeasured_WhenUploadedDownloadedAndDeleted()
    {
        IResourceContainerClient container = await NewContainerAsync("large");
        long length = FileLength;
        string size = $"{length / Gibibyte}GiB";
        // Each operation once at a small size first, so EF Core compiling its queries isn't measured.
        await container.UploadAsync("warm-up", new GeneratedStream(1024 * 1024));
        await using (Stream warmUp = (await container.DownloadStreamingAsync("warm-up"))!.Stream)
            await warmUp.CopyToAsync(Stream.Null);
        await container.DeleteResourceIfExistsAsync("warm-up");

        await MeasureAsync($"upload-{size}", () => container.UploadAsync("r", new GeneratedStream(length)));
        await MeasureAsync($"download-{size}", async () =>
        {
            ResourceDownloadResult? download = await container.DownloadStreamingAsync("r");
            await using Stream content = download!.Stream;
            await content.CopyToAsync(Stream.Null);
        });
        await MeasureAsync($"delete-{size}", () => container.DeleteResourceIfExistsAsync("r"));
    }

    [SkippableFact]
    public async Task ContainerOfManyNames_ShouldBeMeasured_WhenReadListedLimitedSweptAndDeleted()
    {
        // A size limit, so an upload adds up what the container holds.
        var limits = new ResourceContainerPolicy { MaxContainerSize = ResourceSize.Terabytes(1) };
        IResourceContainerClient container =
            await NewContainerAsync("names", options => options.UseContainerPolicies(limits));
        await SeedAsync(container);
        IResourceServiceClient service = _provider!.GetRequiredService<IResourceServiceClient>();
        string rows = Rows >= 1_000_000 && Rows % 1_000_000 == 0 ? $"{Rows / 1_000_000}M" : $"{Rows}";
        // Each operation once on other names first, so EF Core compiling its queries isn't measured.
        await container.GetResourceInfoAsync(NameOf(0));
        await container.GetResourcePageAsync(new ResourceListOptions { Prefix = NameOf(0)[..^1], PageSize = 100 });
        await container.UploadAsync("warm-up", BinaryData.FromString("x"));
        await service.DeleteExpiredAsync();
        IResourceContainerClient warmUp =
            await service.CreateResourceContainerAsync($"{container.ResourceContainerInfo.Name}-warm-up");
        await warmUp.UploadAsync("r", BinaryData.FromString("x"));
        await service.ExportContainerAsync(warmUp.ResourceContainerInfo.Name, Stream.Null);
        await service.DeleteResourceContainerIfExistsAsync(warmUp.ResourceContainerInfo.Name);

        await MeasureAsync($"get-info-of-{rows}", () => container.GetResourceInfoAsync(NameOf(Rows / 2)));
        var middlePage = new ResourceListOptions { Prefix = NameOf(Rows / 2)[..^1], PageSize = 100 };
        await MeasureAsync($"list-page-in-{rows}", () => container.GetResourcePageAsync(middlePage));
        await MeasureAsync($"upload-with-limits-in-{rows}",
            () => container.UploadAsync("new", BinaryData.FromString("x")));
        await MeasureAsync($"expire-nothing-due-in-{rows}", () => service.DeleteExpiredAsync());
        // Every seeded name shares one content, which an export writes once, so this measures reading the container
        // and writing its manifest.
        await MeasureAsync($"export-of-{rows}",
            () => service.ExportContainerAsync(container.ResourceContainerInfo.Name, Stream.Null));
        await MeasureAsync($"delete-container-of-{rows}",
            () => service.DeleteResourceContainerIfExistsAsync(container.ResourceContainerInfo.Name));
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _activity.Dispose();
    }

    private static string NameOf(int row) => $"r{row:D7}";

    /// <summary>
    /// An environment variable's whole number, or <paramref name="unset"/> when it isn't set; anything else fails the
    /// run at once rather than minutes in.
    /// </summary>
    private static long Setting(string name, long unset, long min, long max) =>
        Environment.GetEnvironmentVariable(name) is not { Length: > 0 } text ? unset
        : long.TryParse(text, out long value) && value >= min && value <= max ? value
        : throw new ArgumentOutOfRangeException(name, text,
            $"{name} must be a whole number from {min:#,0} to {max:#,0}.");

    /// <summary>A new container in a database of its own, which it creates and migrates.</summary>
    private async Task<IResourceContainerClient> NewContainerAsync(string database,
        Action<ResourcefullOptionsBuilder>? configure = null)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RESOURCEFULL_SCALE") == "true",
            "Scale tests run only when RESOURCEFULL_SCALE is true.");
        fixture.SkipIfUnavailable();

        _connectionString = fixture.AllocateDatabase($"scale-{database}");
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseSqlServer(_connectionString).UseVersioning(maxVersions: 50);
            configure?.Invoke(options);
        });
        services.ConfigureDbContext<ResourceDbContext>(options => options.AddInterceptors(_activity));
        _provider = services.BuildServiceProvider();
        await _provider.MigrateResourcefullDatabaseAsync();
        return await _provider.GetRequiredService<IResourceServiceClient>()
            .CreateResourceContainerAsync($"scale-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Fills the container with <see cref="Rows"/> names, each a copy of one small version uploaded through the client,
    /// inserted set-based a batch at a time, which takes seconds rather than a million uploads. Every column but the
    /// name, the version ID and the identity is copied, whatever columns migrations have added.
    /// </summary>
    private async Task SeedAsync(IResourceContainerClient container)
    {
        await container.UploadAsync("template", BinaryData.FromString("x"));
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandTimeout = 600;
        command.CommandText = "SELECT STRING_AGG(QUOTENAME([name]), ', ') FROM sys.columns " +
            "WHERE [object_id] = OBJECT_ID('Resources') AND [is_identity] = 0 AND [is_computed] = 0 " +
            "AND [name] NOT IN ('Name', 'VersionId');";
        string columns = (string)(await command.ExecuteScalarAsync())!;
        string copied = string.Join(", ", columns.Split(", ").Select(column => $"t.{column}"));
        command.CommandText = $"""
            DECLARE @lastId bigint = (SELECT MAX([Id]) FROM [Resources]);
            WITH [n] AS (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 + @start AS [i]
                FROM sys.all_columns a CROSS JOIN sys.all_columns b)
            INSERT INTO [Resources] ([Name], [VersionId], {columns})
            SELECT CONCAT('r', RIGHT(CONCAT('000000', [n].[i]), 7)), CONVERT(nvarchar(36), NEWID()), {copied}
            FROM [n] CROSS JOIN [Resources] t WHERE t.[ContainerName] = @container AND t.[Name] = 'template';
            INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data])
            SELECT r.[Id], c.[Index], c.[Data] FROM [Resources] r
            CROSS JOIN [ResourceChunks] c JOIN [Resources] t ON t.[Id] = c.[ResourceId]
            WHERE r.[Id] > @lastId AND t.[ContainerName] = @container AND t.[Name] = 'template';
            """;
        command.Parameters.Add(new SqlParameter("@container", container.ResourceContainerInfo.Name));
        SqlParameter start = command.Parameters.Add(new SqlParameter("@start", System.Data.SqlDbType.Int));
        SqlParameter count = command.Parameters.Add(new SqlParameter("@count", System.Data.SqlDbType.Int));

        var seeding = Stopwatch.StartNew();
        for (int row = 0; row < Rows; row += RowsPerSeedBatch)
        {
            start.Value = row;
            count.Value = Math.Min(RowsPerSeedBatch, Rows - row);
            await command.ExecuteNonQueryAsync();
        }

        // Checked, so a seed that copied nothing isn't reported as a full container.
        command.CommandText = "SELECT COUNT_BIG(*) FROM [Resources] WHERE [ContainerName] = @container;";
        long seeded = (long)(await command.ExecuteScalarAsync())! - 1;
        output.WriteLine($"Seeded {seeded:#,0} names in {seeding.Elapsed.TotalSeconds:F1} s.");
        Assert.Equal(Rows, seeded);
    }

    /// <summary>Runs <paramref name="run"/> once, then reports and records what it cost.</summary>
    private async Task MeasureAsync(string scenario, Func<Task> run)
    {
        // Flushes what the setup dirtied, such as the seed or the upload before a download, so SQL Server doesn't write
        // it out while the scenario is timed.
        await using (SqlConnection connection = new(_connectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "CHECKPOINT;";
            await checkpoint.ExecuteNonQueryAsync();
        }
        ServerCounters before = await ServerCounters.ReadAsync(_connectionString);
        long liveBefore = GC.GetTotalMemory(forceFullCollection: true);
        _activity.Reset();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        await run();

        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        // Read before the counters' own query, which would count as a round trip.
        long roundTrips = _activity.RoundTrips, logicalReads = _activity.LogicalReads;
        long retained = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - liveBefore);
        ServerCounters after = await ServerCounters.ReadAsync(_connectionString);

        var measured = new ScaleMeasurement(roundTrips, logicalReads, after.LogBytes - before.LogBytes,
            after.TempdbPages - before.TempdbPages, after.VersionStorePages - before.VersionStorePages,
            after.LockRequests - before.LockRequests, after.LockEscalations - before.LockEscalations, allocated,
            retained, Math.Round(stopwatch.Elapsed.TotalSeconds, 2));
        output.WriteLine($"{scenario}: {measured}");
        Baseline.Record(ResultsPath, scenario, measured);
    }

    /// <summary>What one scenario cost, measured once.</summary>
    private sealed record ScaleMeasurement(long RoundTrips, long LogicalReads, long LogBytes, long TempdbPages,
        long VersionStorePages, long LockRequests, long LockEscalations, long AllocatedBytes, long RetainedBytes,
        double Seconds);

    /// <summary>
    /// The server's running totals: what the scenario's database has written to its log; the tempdb pages its sessions
    /// have allocated, for sorts, spills and temporary tables, which pooled connections keep counting; the pages the
    /// version store holds, which read-committed snapshot fills as rows change; and how many row and page locks the
    /// database has taken, and how many times SQL Server has escalated them to the whole table.
    /// </summary>
    private sealed record ServerCounters(long LogBytes, long TempdbPages, long VersionStorePages, long LockRequests,
        long LockEscalations)
    {
        public static async Task<ServerCounters> ReadAsync(string connectionString)
        {
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqlCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT [num_of_bytes_written] FROM sys.dm_io_virtual_file_stats(DB_ID(), 2)),
                    (SELECT SUM([user_objects_alloc_page_count] + [internal_objects_alloc_page_count])
                        FROM sys.dm_db_session_space_usage),
                    (SELECT SUM([version_store_reserved_page_count]) FROM tempdb.sys.dm_db_file_space_usage),
                    ISNULL(SUM([row_lock_count] + [page_lock_count]), 0),
                    ISNULL(SUM([index_lock_promotion_count]), 0)
                FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL);
                """;
            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return new ServerCounters(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4));
        }
    }
}
