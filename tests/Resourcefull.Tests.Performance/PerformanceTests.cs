using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;
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

    private readonly SqlActivity _activity = new();
    private ServiceProvider? _provider;

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
    public async Task GetResourceInfoAsync_ShouldStayWithinItsBaseline_WhenTheResourceExists()
    {
        IResourceContainerClient container = await NewContainerAsync();
        await container.UploadAsync("r", new GeneratedStream(Mebibyte));

        await MeasureAsync("get-info", 0, run: _ => container.GetResourceInfoAsync("r"));
    }

    [SkippableFact]
    public async Task GetResourcesAsync_ShouldStayWithinItsBaseline_WhenTheContainerHoldsAHundredResources()
    {
        IResourceContainerClient container = await NewContainerAsync();
        for (int i = 0; i < 100; i++) await container.UploadAsync($"r{i}", new GeneratedStream(1024));

        await MeasureAsync("list-100", 0, run: _ => container.GetResourcesAsync());
    }

    [SkippableFact]
    public async Task GetResourceVersionsAsync_ShouldStayWithinItsBaseline_WhenTheResourceHasTwentyVersions()
    {
        IResourceContainerClient container = await NewContainerAsync();
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

    public void Dispose() => _provider?.Dispose();

    private async Task<IResourceContainerClient> NewContainerAsync()
    {
        fixture.SkipIfUnavailable();

        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(fixture.ConnectionString).UseVersioning(maxVersions: 50));
        services.ConfigureDbContext<ResourceDbContext>(options => options.AddInterceptors(_activity));
        _provider = services.BuildServiceProvider();

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

        var samples = new List<Measurement>();
        for (int iteration = 1; iteration <= Repetitions; iteration++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _activity.Reset();
            using var heap = new PeakHeapSampler();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();

            await run(iteration);

            stopwatch.Stop();
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            samples.Add(new Measurement(_activity.RoundTrips, _activity.LogicalReads, allocated, heap.Stop(),
                stopwatch.Elapsed.TotalSeconds, contentBytes));
        }

        Baseline.Check(scenario, new Measurement(
            Median(samples, s => s.RoundTrips), Median(samples, s => s.LogicalReads), Median(samples, s => s.AllocatedBytes),
            Median(samples, s => s.PeakHeapBytes), Math.Round(Median(samples, s => s.Seconds), 4), contentBytes), output);
    }

    private static T Median<T>(List<Measurement> samples, Func<Measurement, T> metric)
        => samples.Select(metric).Order().ElementAt(samples.Count / 2);

    /// <summary>
    /// Tracks how far the managed heap grows above where it started, from a thread of its own that allocates nothing,
    /// so it doesn't add to the allocations being measured.
    /// </summary>
    private sealed class PeakHeapSampler : IDisposable
    {
        private readonly long _start = GC.GetTotalMemory(forceFullCollection: false);
        private readonly Thread _thread;
        private long _peak;
        private volatile bool _stopped;

        public PeakHeapSampler()
        {
            _peak = _start;
            _thread = new Thread(() =>
            {
                while (!_stopped)
                {
                    _peak = Math.Max(_peak, GC.GetTotalMemory(forceFullCollection: false));
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public long Stop()
        {
            _stopped = true;
            _thread.Join();
            return Math.Max(_peak, GC.GetTotalMemory(forceFullCollection: false)) - _start;
        }

        public void Dispose()
        {
            if (!_stopped) Stop();
        }
    }

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
