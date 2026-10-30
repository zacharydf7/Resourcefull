using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Utilities;
using Resourcefull.Tests.Contract;
using Resourcefull.Tests.Integration;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Resourcefull.Tests.Performance;

/// <summary>
/// Measures what each operation costs through the public clients against a real SQL Server, and holds it to the
/// committed <see cref="Baseline"/>. Every scenario runs once to warm up and then <see cref="Repetitions"/> times,
/// and the medians are compared, so one slow repetition doesn't fail a run.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PerformanceTests(SqlServerFixture fixture, ITestOutputHelper output) : IDisposable
{
    private const int Repetitions = 5;
    private const int Mebibyte = 1024 * 1024;
    private const int ConcurrentClients = 8;

    private readonly SqlActivity _activity = new();
    private ServiceProvider? _provider;
    // The database the scenario runs in, which its checkpoint and log measurement read.
    private string? _connectionString;

    [ModuleInitializer]
    internal static void UseTheSameNetworkingEverywhere()
        // CI runs on Linux, where SqlClient always uses its managed networking; using it on Windows too keeps
        // allocations comparable with the baseline.
        => AppContext.SetSwitch("Switch.Microsoft.Data.SqlClient.UseManagedNetworkingOnWindows", true);

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    public async Task UploadAsync_ShouldStayWithinItsBaseline_WhenGivenContentOfThisManyMebibytes(int mebibytes)
    {
        IResourceContainerClient container = await NewContainerAsync();
        long length = (long)mebibytes * Mebibyte;

        await MeasureAsync($"upload-{mebibytes}MiB", length,
            run: iteration => container.UploadAsync($"r{iteration}", new GeneratedStream(length)));
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    public async Task DownloadStreamingAsync_ShouldStayWithinItsBaseline_WhenReadingContentOfThisManyMebibytes(int mebibytes)
    {
        IResourceContainerClient container = await NewContainerAsync();
        long length = (long)mebibytes * Mebibyte;
        await container.UploadAsync("r", new GeneratedStream(length));

        await MeasureAsync($"download-{mebibytes}MiB", length, run: async _ =>
        {
            ResourceDownloadResult? download = await container.DownloadStreamingAsync("r");
            await using Stream content = download!.Stream;
            await content.CopyToAsync(Stream.Null);
        });
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldStayWithinItsBaseline_WhenReadingTheLastMebibyteOf64()
    {
        // A range should cost about what download-1MiB costs, not what reading up to it would.
        IResourceContainerClient container = await NewContainerAsync();
        long length = 64L * Mebibyte;
        await container.UploadAsync("r", new GeneratedStream(length));

        await MeasureAsync("download-range-1MiB-of-64MiB", Mebibyte, run: async _ =>
        {
            ResourceDownloadResult? download = await container.DownloadStreamingAsync("r", null, new ResourceRange(length - Mebibyte, Mebibyte),
                CancellationToken.None);
            await using Stream content = download!.Stream;
            await content.CopyToAsync(Stream.Null);
        });
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldStayWithinItsBaseline_WhenCopying16MebibytesToAnotherContainer()
    {
        // The server copies the chunks, so no content goes through the application: compare upload-16MiB.
        IResourceContainerClient source = await NewContainerAsync();
        IResourceContainerClient destination = await NewContainerAsync();
        long length = 16L * Mebibyte;
        await source.UploadAsync("r", new GeneratedStream(length));

        await MeasureAsync("copy-16MiB", length,
            run: iteration => source.CopyResourceAsync("r", destination.ResourceContainerInfo.Name, $"r{iteration}"));
    }

    [SkippableFact]
    public async Task DeleteExpiredAsync_ShouldStayWithinItsBaseline_WhenNothingHasExpired()
    {
        // What expiry costs every interval on every instance when there's nothing to delete: a read of the index of
        // versions that expire, whatever else the store holds. In a database of its own, so its versions can't change
        // where other scenarios' land on pages, whatever order the scenarios run in.
        IResourceContainerClient container = await NewContainerAsync(ownDatabase: "expiry");
        for (int i = 0; i < 200; i++) await container.UploadAsync($"lasting{i}", BinaryData.FromString("x"));
        for (int i = 0; i < 20; i++)
            await container.UploadAsync($"expiring{i}", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        IResourceServiceClient service = _provider!.GetRequiredService<IResourceServiceClient>();

        await MeasureAsync("expire-nothing-due", 0, run: _ => service.DeleteExpiredAsync());
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldCountTheReadsOfItsContent_WhenReadingAMebibyte()
    {
        // The baseline catches reads that grow, not reads that stop being counted: a page query whose reader is
        // disposed without reaching the end of its results loses the STATISTICS IO message carrying its reads.
        IResourceContainerClient container = await NewContainerAsync();
        await container.UploadAsync("r", new GeneratedStream(Mebibyte));
        _activity.Reset();

        ResourceDownloadResult? download = await container.DownloadStreamingAsync("r");
        await using (Stream content = download!.Stream) await content.CopyToAsync(Stream.Null);

        // Every full chunk lies on at least five pages.
        long fullChunks = Mebibyte / ResourceChunker.ChunkSize;
        Assert.True(_activity.LogicalReads >= fullChunks * 5,
            $"Only {_activity.LogicalReads} logical reads were counted for {fullChunks} full chunks.");
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldTakeNoMorePagesThanItsContentFills_WhenGiven16Mebibytes()
    {
        // Content pages hold 8,040 bytes each. In a database of its own, so nothing else's pages are counted.
        const int ContentBytesPerPage = 8_040;
        IResourceContainerClient container = await NewContainerAsync(ownDatabase: "storage");
        long length = 16L * Mebibyte;
        await container.UploadAsync("r", new GeneratedStream(length));

        await using SqlConnection connection = await OpenScenarioDatabaseAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SUM([lob_used_page_count]) FROM sys.dm_db_partition_stats " +
            "WHERE [object_id] = OBJECT_ID('ResourceChunks');";
        long pages = (long)(await command.ExecuteScalarAsync())!;

        // A few pages over for the table's own allocation pages.
        long contentPages = (length + ContentBytesPerPage - 1) / ContentBytesPerPage;
        output.WriteLine($"{pages:#,0} content pages for {length:#,0} bytes, which fill {contentPages:#,0}.");
        Assert.True(pages <= contentPages + 8,
            $"{pages:#,0} content pages were used for {contentPages:#,0} pages of content.");
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldStayWithinItsBaseline_WhenEightClientsDownloadDifferentNamesAtOnce()
    {
        IResourceContainerClient container = await NewContainerAsync();
        long length = 4L * Mebibyte;
        for (int client = 0; client < ConcurrentClients; client++) await container.UploadAsync($"r{client}", new GeneratedStream(length));

        await MeasureAsync($"download-{ConcurrentClients}-at-once-4MiB", ConcurrentClients * length,
            run: _ => Task.WhenAll(Enumerable.Range(0, ConcurrentClients).Select(async client =>
            {
                ResourceDownloadResult? download = await container.DownloadStreamingAsync($"r{client}");
                await using Stream content = download!.Stream;
                await content.CopyToAsync(Stream.Null);
            })));
    }

    [SkippableFact]
    public async Task UploadAndDownload_ShouldStayWithinTheirBaselines_WhenTelemetryIsListening()
    {
        // The same operations as upload-16MiB and download-16MiB, with every span and measurement recorded, so the
        // cost of telemetry while it's in use is held to a budget too.
        IResourceContainerClient container = await NewContainerAsync();
        long length = 16L * Mebibyte;
        using TelemetryListeners listening = new();

        await MeasureAsync("upload-16MiB-traced", length,
            run: iteration => container.UploadAsync($"r{iteration}", new GeneratedStream(length)));
        await MeasureAsync("download-16MiB-traced", length, run: async _ =>
        {
            ResourceDownloadResult? download = await container.DownloadStreamingAsync("r0");
            await using Stream content = download!.Stream;
            await content.CopyToAsync(Stream.Null);
        });
    }

    /// <summary>Listens to every Resourcefull span and instrument, as an application exporting them would, and drops what it hears.</summary>
    private sealed class TelemetryListeners : IDisposable
    {
        private readonly ActivityListener _spans = new()
        {
            ShouldListenTo = source => source.Name == ResourcefullTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        private readonly MeterListener _instruments = new()
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ResourcefullTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            }
        };

        public TelemetryListeners()
        {
            ActivitySource.AddActivityListener(_spans);
            _instruments.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _instruments.SetMeasurementEventCallback<long>((_, _, _, _) => { });
            _instruments.Start();
        }

        public void Dispose()
        {
            _spans.Dispose();
            _instruments.Dispose();
        }
    }

    [SkippableFact]
    public async Task GetResourceInfoAsync_ShouldStayWithinItsBaseline_WhenTheResourceExists()
    {
        IResourceContainerClient container = await NewContainerAsync();
        await container.UploadAsync("r", new GeneratedStream(Mebibyte));

        await MeasureAsync("get-info", 0, run: _ => container.GetResourceInfoAsync("r"));
    }

    [SkippableFact]
    public async Task GetResourcesAsync_ShouldStayWithinItsBaseline_WhenTheContainerHoldsAHundredResources()
    {
        IResourceContainerClient container = await NewContainerAsync(ownDatabase: "list");
        for (int i = 0; i < 100; i++) await container.UploadAsync($"r{i}", new GeneratedStream(1024));

        await MeasureAsync("list-100", 0, run: _ => container.GetResourcesAsync());
    }

    [SkippableFact]
    public async Task GetResourcePageAsync_ShouldStayWithinItsBaseline_WhenReadingAPageFromTheMiddleOfFiveHundredResources()
    {
        // A page should cost what its own rows cost, wherever it starts, rather than reading the container up to it.
        IResourceContainerClient container = await NewContainerAsync(ownDatabase: "page");
        for (int i = 0; i < 500; i++) await container.UploadAsync($"r{i:D3}", new GeneratedStream(16));
        var options = new ResourceListOptions { PageSize = 100 };
        string? middle = null;
        for (int page = 0; page < 2; page++) middle = (await container.GetResourcePageAsync(options, middle)).ContinuationToken;

        await MeasureAsync("list-page-100-of-500", 0, run: _ => container.GetResourcePageAsync(options, middle));
    }

    [SkippableFact]
    public async Task GetResourceVersionsAsync_ShouldStayWithinItsBaseline_WhenTheResourceHasTwentyVersions()
    {
        IResourceContainerClient container = await NewContainerAsync(ownDatabase: "versions");
        for (int i = 0; i < 20; i++) await container.UploadAsync("r", new GeneratedStream(1024));

        await MeasureAsync("versions-20", 0, run: _ => container.GetResourceVersionsAsync("r"));
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldStayWithinItsBaseline_WhenDeletingSixteenMebibytes()
    {
        IResourceContainerClient container = await NewContainerAsync();
        for (int i = 0; i <= Repetitions; i++) await container.UploadAsync($"r{i}", new GeneratedStream(16 * Mebibyte));

        await MeasureAsync("delete-16MiB", 0, run: iteration => container.DeleteResourceIfExistsAsync($"r{iteration}"));
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldStayWithinItsBaseline_WhenDeletingANameWithTwentyVersions()
    {
        // The versions go in batches that each commit, oldest first.
        IResourceContainerClient container = await NewContainerAsync();
        for (int i = 0; i <= Repetitions; i++)
        {
            for (int version = 0; version < 20; version++)
                await container.UploadAsync($"r{i}", new GeneratedStream(Mebibyte));
        }

        await MeasureAsync("delete-20-versions-1MiB", 0,
            run: iteration => container.DeleteResourceIfExistsAsync($"r{iteration}"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldStayWithinItsBaseline_WhenReplacingSixteenMebibytesWithoutVersioning()
    {
        // Without versioning, the version replaced is deleted in the same upload: compare upload-16MiB and
        // delete-16MiB.
        IResourceContainerClient container = await NewContainerAsync(versioned: false);
        long length = 16L * Mebibyte;
        var overwrite = new ResourceUploadOptions { Overwrite = true };
        await container.UploadAsync("r", new GeneratedStream(length));

        await MeasureAsync("overwrite-16MiB", length,
            run: _ => container.UploadAsync("r", new GeneratedStream(length), overwrite));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldStayWithinItsBaseline_WhenTheContainerHasLimits()
    {
        // What checking a container's limits adds to an upload: compare upload-16MiB.
        var limits = new ResourceContainerPolicy
        {
            MaxResourceSize = ResourceSize.Megabytes(64),
            MaxContainerSize = ResourceSize.Gigabytes(1),
            MaxResourceCount = 1_000
        };
        IResourceContainerClient container =
            await NewContainerAsync(configure: options => options.UseContainerPolicies(limits));
        long length = 16L * Mebibyte;

        await MeasureAsync("upload-16MiB-with-limits", length,
            run: iteration => container.UploadAsync($"r{iteration}", new GeneratedStream(length)));
    }

    [SkippableFact]
    public async Task RestoreVersionAsync_ShouldStayWithinItsBaseline_WhenRestoringSixteenMebibytes()
    {
        // A restore copies the old version's chunks in the database: compare copy-16MiB.
        IResourceContainerClient container = await NewContainerAsync();
        long length = 16L * Mebibyte;
        string first = (await container.UploadAsync("r", new GeneratedStream(length))).VersionId;
        await container.UploadAsync("r", new GeneratedStream(1024));

        await MeasureAsync("restore-16MiB", length, run: _ => container.RestoreVersionAsync("r", first));
    }

    [SkippableFact]
    public async Task MoveResourceAsync_ShouldStayWithinItsBaseline_WhenMovingSixteenMebibytes()
    {
        // A move changes only the versions' name, whatever their size.
        IResourceContainerClient container = await NewContainerAsync();
        string containerName = container.ResourceContainerInfo.Name;
        for (int i = 0; i <= Repetitions; i++) await container.UploadAsync($"r{i}", new GeneratedStream(16 * Mebibyte));

        await MeasureAsync("move-16MiB", 0,
            run: iteration => container.MoveResourceAsync($"r{iteration}", containerName, $"moved{iteration}"));
    }

    [SkippableFact]
    public async Task ExportAndImport_ShouldStayWithinTheirBaselines_WhenTheContainerHoldsSixteenMebibytes()
    {
        // In a database of its own, since both list the container's rows. Each resource a byte longer than the last, so
        // their contents differ: an export stores identical content once.
        IResourceContainerClient container = await NewContainerAsync(ownDatabase: "export");
        long length = 0;
        for (int i = 0; i < 4; i++)
        {
            await container.UploadAsync($"r{i}", new GeneratedStream(4 * Mebibyte + i));
            length += 4 * Mebibyte + i;
        }
        IResourceServiceClient service = _provider!.GetRequiredService<IResourceServiceClient>();
        string name = container.ResourceContainerInfo.Name;
        var export = new MemoryStream();
        await service.ExportContainerAsync(name, export);

        await MeasureAsync("export-16MiB", length,
            run: _ => service.ExportContainerAsync(name, Stream.Null));
        await MeasureAsync("import-16MiB", length, run: iteration =>
        {
            export.Position = 0;
            return service.ImportContainerAsync($"{name}-imported{iteration}", export);
        });
    }

    [SkippableFact]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldStayWithinItsBaseline_WhenTheContainerHoldsSixteenMebibytes()
    {
        // In a database of its own, since it lists the container's rows.
        IResourceContainerClient first = await NewContainerAsync(ownDatabase: "containers");
        IResourceServiceClient service = _provider!.GetRequiredService<IResourceServiceClient>();
        var names = new List<string> { first.ResourceContainerInfo.Name };
        for (int i = 1; i <= Repetitions; i++)
            names.Add((await service.CreateResourceContainerAsync($"{names[0]}-{i}")).ResourceContainerInfo.Name);
        foreach (string name in names)
        {
            IResourceContainerClient container = service.GetResourceContainerClient(name);
            for (int i = 0; i < 4; i++) await container.UploadAsync($"r{i}", new GeneratedStream(4 * Mebibyte));
        }

        await MeasureAsync("delete-container-16MiB", 0,
            run: iteration => service.DeleteResourceContainerIfExistsAsync(names[iteration]));
    }

    [SkippableFact]
    public async Task DeleteExpiredAsync_ShouldStayWithinItsBaseline_WhenSixteenVersionsAreDue()
    {
        // Each repetition moves the clock an hour on, so the next group of 16 versions of 1 MiB becomes due. In a
        // database of its own, since the sweep reads the index of versions that expire.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        DateTimeOffset start = clock.Now;
        IResourceContainerClient container = await NewContainerAsync(ownDatabase: "expiry-due",
            configure: options => options.Services.AddSingleton<TimeProvider>(clock));
        for (int group = 0; group <= Repetitions; group++)
        {
            var due = new ResourceUploadOptions { ExpiresAt = start.AddHours(group + 1) };
            for (int i = 0; i < 16; i++)
                await container.UploadAsync($"g{group}-r{i}", new GeneratedStream(Mebibyte), due);
        }
        IResourceServiceClient service = _provider!.GetRequiredService<IResourceServiceClient>();

        await MeasureAsync("expire-16-due-1MiB", 0, run: iteration =>
        {
            clock.Now = start.AddHours(iteration + 1).AddSeconds(1);
            return service.DeleteExpiredAsync();
        });
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldStayWithinItsBaseline_WhenEightClientsUploadDifferentNamesAtOnce()
    {
        IResourceContainerClient container = await NewContainerAsync();
        long length = 4L * Mebibyte;

        await MeasureAsync($"upload-{ConcurrentClients}-at-once-4MiB", ConcurrentClients * length,
            run: iteration => Task.WhenAll(Enumerable.Range(0, ConcurrentClients).Select(client =>
                container.UploadAsync($"r{iteration}-{client}", new GeneratedStream(length)))));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldStayWithinItsBaseline_WhenEightClientsUploadTheSameNameAtOnce()
    {
        // Each upload waits for the others' turn on the name's lock only to make its version current. A new name for
        // each repetition, so every one starts from the same empty history.
        IResourceContainerClient container = await NewContainerAsync();
        long length = Mebibyte;

        await MeasureAsync($"upload-same-name-{ConcurrentClients}-at-once-1MiB", ConcurrentClients * length,
            run: iteration => Task.WhenAll(Enumerable.Range(0, ConcurrentClients).Select(_ =>
                container.UploadAsync($"r{iteration}", new GeneratedStream(length)))));
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _activity.Dispose();
    }

    /// <summary>
    /// A new container, in the shared database, or in a database of the scenario's own, named for
    /// <paramref name="ownDatabase"/>, which it creates and migrates. A scenario that reads a range of rows has its own,
    /// so how its rows lie on pages doesn't depend on what the scenarios before it stored, nor theirs on what it stores.
    /// </summary>
    private async Task<IResourceContainerClient> NewContainerAsync(string? ownDatabase = null,
        Action<ResourcefullOptionsBuilder>? configure = null, bool versioned = true)
    {
        fixture.SkipIfUnavailable();

        _connectionString = ownDatabase is null ? fixture.ConnectionString : fixture.AllocateDatabase(ownDatabase);
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseSqlServer(_connectionString);
            if (versioned) options.UseVersioning(maxVersions: 50);
            configure?.Invoke(options);
        });
        services.ConfigureDbContext<ResourceDbContext>(options => options.AddInterceptors(_activity));
        _provider = services.BuildServiceProvider();
        if (ownDatabase is not null) await _provider.MigrateResourcefullDatabaseAsync();

        var resources = _provider.GetRequiredService<IResourceServiceClient>();
        return await resources.CreateResourceContainerAsync($"perf-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Runs <paramref name="run"/> once to warm up, then <see cref="Repetitions"/> times measured, passing the
    /// repetition's number (0 for the warm-up), and checks the medians against the baseline.
    /// </summary>
    private async Task MeasureAsync(string scenario, long contentBytes, Func<int, Task> run)
    {
        await run(0);

        // After the warm-up, so the cleanup of what it deleted isn't measured either.
        await WaitForGhostCleanupAsync();

        // Flushes the pages the scenario's setup and warm-up dirtied, so SQL Server doesn't write them out during the
        // measured runs: on a slow disk that more than tripled the time of deletes measured right after their uploads.
        await using (SqlConnection connection = await OpenScenarioDatabaseAsync())
        await using (SqlCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "CHECKPOINT;";
            await checkpoint.ExecuteNonQueryAsync();
        }

        // The log is read around all the repetitions rather than each one: reading it between repetitions added
        // allocations to the next one's measurements. Each repetition logs the same, so the average is what one logs.
        long logBefore = await LogBytesWrittenAsync();
        var samples = new List<Measurement>();
        for (int iteration = 1; iteration <= Repetitions; iteration++)
        {
            long liveBefore = GC.GetTotalMemory(forceFullCollection: true);
            _activity.Reset();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();

            await run(iteration);

            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            // What the operation still holds once it's done, after collecting the heap, so garbage doesn't count. Below
            // zero when what the repetition before it left, such as a pooled connection's buffers, was let go.
            long retained = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - liveBefore);
            samples.Add(new Measurement(_activity.RoundTrips, _activity.LogicalReads, 0, allocated, retained,
                stopwatch.Elapsed.TotalSeconds, contentBytes));
        }
        long logBytes = (await LogBytesWrittenAsync() - logBefore) / Repetitions;

        Baseline.Check(scenario, new Measurement(
            Median(samples, s => s.RoundTrips), Median(samples, s => s.LogicalReads), logBytes,
            Median(samples, s => s.AllocatedBytes), Median(samples, s => s.RetainedBytes),
            Math.Round(Median(samples, s => s.Seconds), 4), contentBytes), output);
    }

    /// <summary>
    /// Waits until SQL Server's background ghost cleanup has removed the rows deleted in the scenario's database, by
    /// earlier scenarios or by this one's warm-up. It runs seconds after a delete and logs as it goes, about 6 MB for
    /// 96 MiB of deleted content, which would otherwise count against whichever scenario happens to be measured then.
    /// Scanning the pages also queues them for cleanup. A few ghosts can linger; cleaning one logs about 360 bytes, so
    /// the few left log far less than the gate's slack.
    /// </summary>
    private async Task WaitForGhostCleanupAsync()
    {
        const long LingeringGhosts = 64;
        var waited = Stopwatch.StartNew();
        await using SqlConnection connection = await OpenScenarioDatabaseAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ISNULL(SUM([ghost_record_count]), 0) FROM sys.dm_db_index_physical_stats(DB_ID(), " +
            "OBJECT_ID('ResourceChunks'), NULL, NULL, 'DETAILED');";
        while (Convert.ToInt64(await command.ExecuteScalarAsync()) > LingeringGhosts)
        {
            if (waited.Elapsed > TimeSpan.FromMinutes(1))
            {
                output.WriteLine("Ghost cleanup was still running after a minute; measuring anyway.");
                return;
            }
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// What SQL Server has written to the test database's transaction log so far. Each write's log records are flushed
    /// by the time it commits, so the difference across a scenario is what it logged.
    /// </summary>
    private async Task<long> LogBytesWrittenAsync()
    {
        await using SqlConnection connection = await OpenScenarioDatabaseAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT [num_of_bytes_written] FROM sys.dm_io_virtual_file_stats(DB_ID(), 2);";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>A raw connection to the database the scenario runs in.</summary>
    private async Task<SqlConnection> OpenScenarioDatabaseAsync()
    {
        var connection = new SqlConnection(_connectionString ?? fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static T Median<T>(List<Measurement> samples, Func<Measurement, T> metric)
        => samples.Select(metric).Order().ElementAt(samples.Count / 2);
}
