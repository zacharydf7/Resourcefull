using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Utilities;
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
    private async Task<IResourceContainerClient> NewContainerAsync(string? ownDatabase = null)
    {
        fixture.SkipIfUnavailable();

        _connectionString = ownDatabase is null ? fixture.ConnectionString : fixture.AllocateDatabase(ownDatabase);
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(_connectionString).UseVersioning(maxVersions: 50));
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
    /// Waits until SQL Server's background ghost cleanup has removed the rows earlier scenarios deleted. It runs seconds
    /// after a delete and logs as it goes, about 6 MB for 96 MiB of deleted content, which would otherwise count
    /// against whichever scenario happens to be measured then. Scanning the pages also queues them for cleanup. A few
    /// ghosts can linger; cleaning one logs about 360 bytes, so the few left log far less than the gate's slack.
    /// </summary>
    private async Task WaitForGhostCleanupAsync()
    {
        const long LingeringGhosts = 64;
        var waited = Stopwatch.StartNew();
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
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

    /// <summary>Content that can't seek and never exists in memory all at once, like a request body.</summary>
    private sealed class GeneratedStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - _position);
            for (int i = 0; i < count; i++) buffer[i] = (byte)(_position + i);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
