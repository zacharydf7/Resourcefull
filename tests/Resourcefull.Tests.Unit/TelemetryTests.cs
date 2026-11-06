using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Resourcefull.InMemory;
using Resourcefull.Providers;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Unit;

/// <summary>Listeners see every test's telemetry, so these tests don't run alongside others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}

[Collection(TelemetryCollection.Name)]
public sealed class TelemetryTests : IDisposable
{
    private static readonly ActivitySource TestSource = new("Resourcefull.Tests");

    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ConcurrentQueue<(string Instrument, double Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener = new();

    public TelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is ResourcefullTelemetry.ActivitySourceName or "Resourcefull.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Enqueue
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ResourcefullTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Collect(instrument, value, tags));
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Collect(instrument, value, tags));
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    private void Collect(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>();
        foreach (KeyValuePair<string, object?> tag in tags) copy[tag.Key] = tag.Value;
        _measurements.Enqueue((instrument.Name, value, copy));
    }

    private static ServiceProvider NewServices(bool recordResourceNames = false) => new ServiceCollection()
        .AddResourcefull(o => o.UseInMemory().UseVersioning().UseTelemetry(t => t.RecordResourceNames = recordResourceNames))
        .BuildServiceProvider();

    /// <summary>The spans started under <paramref name="root"/>, the test's own trace.</summary>
    private List<Activity> SpansIn(Activity root) => [.. _spans.Where(s => s.TraceId == root.TraceId && s.Source.Name == ResourcefullTelemetry.ActivitySourceName)];

    [Fact]
    public async Task EveryClientOperation_ShouldEmitASpan_WhenCalled()
    {
        // Arrange: a container with a version in it, so every operation has something to work on, and in other
        // containers, which the operations on "c" leave alone, an expired version for the expiry sweep to find and one
        // due to leave the bin for the purge to find.
        using ServiceProvider provider = NewServices();
        var service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        ResourceInfo version = await container.UploadAsync("r", BinaryData.FromString("x"));
        await (await service.CreateContainerAsync("expiring")).UploadAsync("r", BinaryData.FromString("x"),
            new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await (await service.CreateContainerAsync("binned")).UploadAsync("r", BinaryData.FromString("x"));
        await provider.GetRequiredService<ResourceRepository>().SoftDeleteResourceAsync(new ResourceAddress("binned", "r"), null, null,
            DateTimeOffset.UtcNow.AddDays(-1));
        var silent = new List<string>();

        // Act: every public method of both client interfaces, except the ones that don't contact storage.
        foreach ((object client, MethodInfo method) in Operations(service, container))
        {
            using Activity root = TestSource.StartActivity(method.Name)!;
            object?[] arguments = [.. method.GetParameters().Select(p => ArgumentFor(p, method, version))];
            // An operation that fails, as some do once others have deleted what they work on, still records its span.
            await Record.ExceptionAsync(async () =>
            {
                object? result = method.Invoke(client, arguments);
                if (result is IAsyncEnumerable<object> listing)
                    await foreach (object _ in listing) { }
                else
                    await (Task)result!;
            });
            if (SpansIn(root).Count == 0) silent.Add($"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
        }

        // Assert
        Assert.Empty(silent);
    }

    private static IEnumerable<(object, MethodInfo)> Operations(IResourceServiceClient service, IResourceContainerClient container)
    {
        foreach (MethodInfo method in typeof(IResourceServiceClient).GetMethods().Where(m => !m.IsSpecialName && m.Name != nameof(IResourceServiceClient.GetContainerClient)))
            yield return (service, method);
        foreach (MethodInfo method in typeof(IResourceContainerClient).GetMethods().Where(m => !m.IsSpecialName && m.Name != nameof(IDisposable.Dispose)))
            yield return (container, method);
    }

    private static object? ArgumentFor(ParameterInfo parameter, MethodInfo method, ResourceInfo version) => parameter.Name switch
    {
        "resourceContainerName" => method.Name.StartsWith("Create", StringComparison.Ordinal) ? $"c-{Guid.NewGuid():N}" : "c",
        "resourceName" => "r",
        "versionId" => version.VersionId,
        "content" when parameter.ParameterType == typeof(Stream) => new MemoryStream([1, 2, 3]),
        "content" => BinaryData.FromString("y"),
        "cancellationToken" => CancellationToken.None,
        _ => null
    };

    [Fact]
    public async Task UploadAsync_ShouldRecordItsDurationBytesAndSpan_WhenItSucceeds()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.UploadAsync("report.txt", BinaryData.FromString("hello"));

        // Assert
        Activity upload = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull upload");
        Assert.Equal("upload", upload.GetTagItem("resourcefull.operation"));
        Assert.Equal("inmemory", upload.GetTagItem("resourcefull.provider"));
        Assert.Equal("c", upload.GetTagItem("resourcefull.container"));
        Assert.Equal(false, upload.GetTagItem("resourcefull.conditional"));
        Assert.Null(upload.GetTagItem("resourcefull.resource"));
        Assert.Contains(SpansIn(root), s => s.OperationName == "resourcefull prune" && s.ParentSpanId == upload.SpanId);
        Assert.Contains(_measurements, m => m.Instrument == "resourcefull.client.operation.duration"
            && Equals(m.Tags["resourcefull.operation"], "upload") && Equals(m.Tags["resourcefull.provider"], "inmemory") && !m.Tags.ContainsKey("error.type"));
        Assert.Contains(_measurements, m => m.Instrument == "resourcefull.client.uploaded" && m.Value == 5);
    }

    [Fact]
    public async Task UploadSession_ShouldRecordASpanForEachCallAndTheBytesAppendedAndStored_WhenCompleted()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        ResourceUpload upload = await container.StartUploadAsync("video.mp4");
        upload = (await container.AppendUploadAsync("video.mp4", upload.UploadId, 0, new MemoryStream(new byte[3])))!;
        await container.AppendUploadAsync("video.mp4", upload.UploadId, upload.Offset, new MemoryStream(new byte[4]));
        await container.CompleteUploadAsync("video.mp4", upload.UploadId);

        // Assert
        List<Activity> calls = [.. SpansIn(root).Where(s => s.ParentSpanId == root.SpanId).OrderBy(s => s.StartTimeUtc)];
        Assert.Equal(["resourcefull upload_start", "resourcefull upload_append", "resourcefull upload_append",
            "resourcefull upload_complete"], calls.Select(s => s.OperationName));
        Assert.Equal([0L, 3L], calls.Where(s => s.OperationName == "resourcefull upload_append")
            .Select(s => (long)s.GetTagItem("resourcefull.offset")!));
        Assert.Equal([3.0, 4.0], _measurements.Where(m => m.Instrument == "resourcefull.client.appended").Select(m => m.Value));
        Assert.Contains(_measurements, m => m.Instrument == "resourcefull.client.uploaded" && m.Value == 7
            && Equals(m.Tags["resourcefull.operation"], "upload_complete"));
    }

    [Fact]
    public async Task AppendUploadAsync_ShouldCountWhatItStored_WhenItFailsPartway()
    {
        // Arrange: a provider that stored 5 bytes of the append before it failed.
        var repository = new Mock<ResourceRepository>();
        var staged = new StagedVersion(new NewResourceVersion(new ResourceAddress("c", "a"), "u1"), 0);
        repository.SetupSequence(r => r.GetStagedVersionAsync(new ResourceAddress("c", "a"), "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(staged)
            .ReturnsAsync(staged with { StoredLength = 5 });
        repository.Setup(r => r.AppendStagedContentAsync(new ResourceAddress("c", "a"), "u1", 0, It.IsAny<Stream>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("The connection dropped."));
        var container = new ResourceContainerClient(repository.Object, null, new ResourcefullOptions(), "c", null);

        // Act
        await Assert.ThrowsAsync<IOException>(() => container.AppendUploadAsync("a", "u1", 0, new MemoryStream(new byte[9])));

        // Assert
        Assert.Equal([5.0], _measurements.Where(m => m.Instrument == "resourcefull.client.appended").Select(m => m.Value));
    }

    [Fact]
    public async Task UploadAsync_ShouldRecordTheErrorType_WhenItFails()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient missing = provider.GetRequiredService<IResourceServiceClient>().GetContainerClient("missing");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(() => missing.UploadAsync("r", BinaryData.FromString("x")));

        // Assert
        string errorType = typeof(ResourceContainerNotFoundException).FullName!;
        Activity upload = Assert.Single(SpansIn(root));
        Assert.Equal(ActivityStatusCode.Error, upload.Status);
        Assert.Equal(errorType, upload.GetTagItem("error.type"));
        Assert.Contains(_measurements, m => m.Instrument == "resourcefull.client.operation.duration" && Equals(m.Tags.GetValueOrDefault("error.type"), errorType));
        Assert.DoesNotContain(_measurements, m => m.Instrument == "resourcefull.client.uploaded");
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("refused")]
    [InlineData("failed")]
    public async Task UploadAsync_ShouldRecordTheInspectionUnderTheUpload_WhenTheInspectorAnswers(string outcome)
    {
        // Arrange
        using ServiceProvider provider = UploadInspectionTests.InspectedServices((_, _) => outcome switch
        {
            "accepted" => Task.FromResult(ResourceInspectionResult.Accepted),
            "refused" => Task.FromResult(ResourceInspectionResult.Refused("No.")),
            _ => throw new InvalidOperationException("The scanner is unavailable.")
        });
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await Record.ExceptionAsync(() => container.UploadAsync("r", BinaryData.FromString("x")));

        // Assert
        Activity upload = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull upload");
        Activity inspect = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull inspect");
        Assert.Equal(upload.SpanId, inspect.ParentSpanId);
        Assert.Equal(outcome, inspect.GetTagItem("resourcefull.inspection_outcome"));
        Assert.Equal(outcome == "failed" ? ActivityStatusCode.Error : ActivityStatusCode.Unset, inspect.Status);
        string? errorType = outcome == "failed" ? typeof(InvalidOperationException).FullName : null;
        Assert.Contains(_measurements, m => m.Instrument == "resourcefull.client.inspection.duration"
            && Equals(m.Tags["resourcefull.inspection_outcome"], outcome) && Equals(m.Tags["resourcefull.operation"], "upload")
            && Equals(m.Tags["resourcefull.provider"], "inmemory") && Equals(m.Tags.GetValueOrDefault("error.type"), errorType));
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldCountTheBytesAsTheyAreRead_WhenTheStreamIsRead()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("report.txt", BinaryData.FromString("hello"));
        ResourceDownloadResult download = (await container.DownloadStreamingAsync("report.txt"))!;
        await using Stream stream = download.Stream;

        // Act
        var buffer = new byte[2];
        await stream.ReadExactlyAsync(buffer);
        double afterTwo = _measurements.Where(m => m.Instrument == "resourcefull.client.downloaded").Sum(m => m.Value);
        await stream.CopyToAsync(Stream.Null);

        // Assert
        Assert.Equal(2, afterTwo);
        Assert.Equal(5, _measurements.Where(m => m.Instrument == "resourcefull.client.downloaded").Sum(m => m.Value));
        Assert.Equal(5, stream.Length);
        Assert.False(stream.CanSeek);
    }

    [Theory]
    [InlineData(6, 5L, "6-10")]
    [InlineData(6, null, "6-")]
    public async Task DownloadStreamingAsync_ShouldTagTheRangeAndCountOnlyItsBytes_WhenGivenARange(long offset, long? length, string tag)
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("report.txt", BinaryData.FromString("hello world"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        ResourceDownloadResult download = (await container.DownloadStreamingAsync("report.txt", null, new ResourceRange(offset, length), CancellationToken.None))!;
        await using (Stream stream = download.Stream)
            await stream.CopyToAsync(Stream.Null);

        // Assert
        Activity span = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull download");
        Assert.Equal(tag, span.GetTagItem("resourcefull.range"));
        Assert.Equal(5, _measurements.Where(m => m.Instrument == "resourcefull.client.downloaded").Sum(m => m.Value));
    }

    [Theory]
    [InlineData(ResourceWriteMode.Create, "create")]
    [InlineData(ResourceWriteMode.Replace, "replace")]
    public async Task UploadAndCopy_ShouldTagTheWriteMode_WhenGivenAMode(ResourceWriteMode mode, string tag)
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("source", BinaryData.FromString("x"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.UploadAsync("upload", BinaryData.FromString("x"), new ResourceUploadOptions { Mode = mode });
        await container.CopyResourceAsync("source", "c", "copy", new ResourceCopyOptions { Mode = mode });

        // Assert
        Assert.Equal(tag, Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull upload").GetTagItem("resourcefull.write_mode"));
        Assert.Equal(tag, Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull copy").GetTagItem("resourcefull.write_mode"));
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldNotTagARange_WhenGivenNone()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("report.txt", BinaryData.FromString("hello world"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        ResourceDownloadResult download = (await container.DownloadStreamingAsync("report.txt"))!;
        await download.Stream.DisposeAsync();

        // Assert
        Activity span = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull download");
        Assert.Null(span.GetTagItem("resourcefull.range"));
    }

    private sealed class TextOnlyProvider : IResourcePolicyProvider
    {
        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ResourceContainerPolicy?>(new ResourceContainerPolicy { AllowedContentTypes = ["text/plain"] });
    }

    [Fact]
    public async Task UploadAsync_ShouldTagTheBrokenRuleAndAskThePolicyProviderOnce_WhenUploadsAreRefused()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UsePolicyProvider<TextOnlyProvider>()).BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;
        var html = new ResourceUploadOptions { Headers = new ResourceHeaders("text/html", null) };

        // Act: the second upload finds the policy cached.
        await Record.ExceptionAsync(() => container.UploadAsync("a.html", BinaryData.FromString("x"), html));
        await Record.ExceptionAsync(() => container.UploadAsync("b.html", BinaryData.FromString("x"), html));

        // Assert
        List<Activity> uploads = [.. SpansIn(root).Where(s => s.OperationName == "resourcefull upload")];
        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, s => Assert.Equal("allowed_content_types", s.GetTagItem("resourcefull.policy_rule")));
        Assert.All(uploads, s => Assert.Equal(typeof(ResourcePolicyViolationException).FullName, s.GetTagItem("error.type")));
        Activity lookup = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull get_policy");
        Assert.Contains(uploads, s => s.SpanId == lookup.ParentSpanId);
    }

    [Fact]
    public async Task CopyAndMove_ShouldTagTheDestinationContainer_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        await service.CreateContainerAsync("d");
        await container.UploadAsync("a", BinaryData.FromString("x"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.CopyResourceAsync("a", "d", "b");
        await container.MoveResourceAsync("a", "d", "c");

        // Assert
        Activity copy = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull copy");
        Activity move = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull move");
        Assert.All(new[] { copy, move }, s => Assert.Equal(("c", "d"),
            (s.GetTagItem("resourcefull.container"), s.GetTagItem("resourcefull.destination_container"))));
    }

    [Fact]
    public async Task CopyContainerToAndCopyStoreToAsync_ShouldTagTheDestinationStore_WhenTheDestinationWrapsANamedStoresClient()
    {
        // Arrange: a named store, reached through a client that wraps the store's own client.
        using ServiceProvider provider = new ServiceCollection().AddResourcefull(o => o.UseInMemory())
            .AddKeyedResourcefull("archive", o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        await (await service.CreateContainerAsync("c")).UploadAsync("a", BinaryData.FromString("x"));
        IResourceServiceClient archive = WrappingClient.Wrap(provider.GetRequiredKeyedService<IResourceServiceClient>("archive"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await service.CopyContainerToAsync("c", archive);
        await service.CopyStoreToAsync(archive, new StoreCopyOptions { Existing = ExistingContainers.SkipMatching });

        // Assert
        Activity container = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull copy_container");
        Activity store = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull copy_store");
        Assert.All(new[] { container, store }, s => Assert.Equal("archive", s.GetTagItem("resourcefull.destination_store")));
    }

    /// <summary>A client that passes every call to another, as a decorator in an application would.</summary>
    public class WrappingClient : DispatchProxy
    {
        private IResourceServiceClient _inner = null!;

        public static IResourceServiceClient Wrap(IResourceServiceClient inner)
        {
            IResourceServiceClient wrapper = Create<IResourceServiceClient, WrappingClient>();
            ((WrappingClient)(object)wrapper)._inner = inner;
            return wrapper;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod!.Invoke(_inner, args);
    }

    [Fact]
    public async Task PropertyChanges_ShouldRecordTheirSpansAndAskThePolicyProvider_WhenCalled()
    {
        // Arrange: no caching, so each change asks the policy provider.
        using ServiceProvider provider = new ServiceCollection().AddResourcefull(o => o.UseInMemory()
            .UseContainerPolicies(cacheFor: TimeSpan.Zero).UsePolicyProvider<NoPolicies>()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.SetResourceMetadataAsync("a", null, new Dictionary<string, string>());
        await container.SetContentTypeAsync("a", null, "text/plain");
        await service.GetContainerClient("c").SetMetadataAsync(new Dictionary<string, string>());

        // Assert
        foreach (string operation in new[] { "set_metadata", "set_content_type", "set_container_metadata" })
        {
            Activity span = Assert.Single(SpansIn(root), s => s.OperationName == $"resourcefull {operation}");
            Assert.Contains(SpansIn(root), s => s.OperationName == "resourcefull get_policy" && s.ParentSpanId == span.SpanId);
        }
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldRecordHowManyVersionsItDeleted_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        ResourceInfo stored = await container.UploadAsync("a", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await container.UploadAsync("b", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.SetExpiryAsync("a", stored.VersionId, DateTimeOffset.UtcNow.AddDays(1));
        await service.DeleteExpiredAsync();

        // Assert
        Activity expire = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull expire");
        Assert.Equal(1L, expire.GetTagItem("resourcefull.expired"));
        Assert.Equal(1, expire.GetTagItem("resourcefull.batches"));
        Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull set_expiry");
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteUploadsAbandonedADayAgoAndRecordHowMany_WhenCalled()
    {
        // Arrange: a provider that found two uploads abandoned, and an expired version, so the sweep records a span.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.DeleteAbandonedUploadsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null,
            It.IsAny<CancellationToken>())).ReturnsAsync([new ResourceInfo
            {
                ContainerName = "c", Name = "a", VersionId = "v1", ExpiresAt = DateTimeOffset.UnixEpoch
            }]);
        var service = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));
        using Activity root = TestSource.StartActivity("test")!;
        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        await service.DeleteExpiredAsync();

        // Assert
        repository.Verify(r => r.DeleteAbandonedUploadsAsync(
            It.Is<DateTimeOffset>(t => t >= before.AddHours(-24) && t <= DateTimeOffset.UtcNow.AddHours(-24)),
            It.IsAny<CancellationToken>()), Times.Once);
        Activity expire = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull expire");
        Assert.Equal(2L, expire.GetTagItem("resourcefull.abandoned_uploads"));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldReportBothKindsOfFailure_WhenAVersionAndDeletingAbandonedUploadsFail()
    {
        // Arrange: an expired version that can't be deleted, in a batch or alone, and an abandoned-upload sweep that
        // fails.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.DeleteAbandonedUploadsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The sweep failed."));
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null,
            It.IsAny<CancellationToken>())).ReturnsAsync([new ResourceInfo
            {
                ContainerName = "c", Name = "a", VersionId = "v1", ExpiresAt = DateTimeOffset.UnixEpoch
            }]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<ResourceVersionAddress>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The batch failed."));
        repository.Setup(r => r.DeleteExpiredVersionAsync(It.IsAny<ResourceAddress>(), "v1", It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("The version failed."));
        var service = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));

        // Act
        Task act() => service.DeleteExpiredAsync();

        // Assert
        var failure = await Assert.ThrowsAsync<AggregateException>(act);
        Assert.Contains("1 expired versions weren't deleted", failure.Message);
        Assert.Contains("The first 1 failures follow, then the one from deleting what stopped uploads left behind",
            failure.Message);
        Assert.Equal(["The version failed.", "The sweep failed."], failure.InnerExceptions.Select(e => e.Message));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteExpiredVersionsAndReport_WhenDeletingAbandonedUploadsFails()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.DeleteAbandonedUploadsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The sweep failed."));
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null,
            It.IsAny<CancellationToken>())).ReturnsAsync([new ResourceInfo
            {
                ContainerName = "c", Name = "a", VersionId = "v1", ExpiresAt = DateTimeOffset.UnixEpoch
            }]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<ResourceVersionAddress>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceVersionAddress(new ResourceAddress("c", "a"), "v1")]);
        var service = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));

        // Act
        Task act() => service.DeleteExpiredAsync();

        // Assert
        Assert.Equal("The sweep failed.", (await Assert.ThrowsAsync<InvalidOperationException>(act)).Message);
        repository.Verify(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<ResourceVersionAddress>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldRecordTheFailedBatchWithoutFailingOrNamingTheResource_WhenItFallsBack()
    {
        // Arrange: a provider whose batch delete fails, naming the resource, but whose single delete works.
        var repository = new Mock<ResourceRepository>();
        var expired = new ResourceInfo
        {
            ContainerName = "c", Name = "secret.txt", VersionId = "v1", ExpiresAt = DateTimeOffset.UnixEpoch
        };
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null,
            It.IsAny<CancellationToken>())).ReturnsAsync([expired]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<ResourceVersionAddress>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Deleting secret.txt failed."));
        repository.Setup(r => r.DeleteExpiredVersionAsync(It.IsAny<ResourceAddress>(), "v1", It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        long deleted = await service.DeleteExpiredAsync();

        // Assert
        Assert.Equal(1, deleted);
        Activity expire = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull expire");
        Assert.Equal(1, expire.GetTagItem("resourcefull.batch_fallbacks"));
        Assert.Equal(ActivityStatusCode.Unset, expire.Status);
        ActivityEvent recorded = Assert.Single(expire.Events, e => e.Name == "exception");
        Assert.DoesNotContain(recorded.Tags, t => t.Value?.ToString()?.Contains("secret.txt") == true);
    }

    [Fact]
    public async Task PruneAsync_ShouldRecordHowManyVersionsWerePastTheLimit_WhenCalled()
    {
        // Arrange: versioning keeps three, then a service keeping one prunes the container.
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("1"));
        await container.UploadAsync("a", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await container.UploadAsync("a", BinaryData.FromString("3"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        var keepingOne = new ResourceServiceClient(provider.GetRequiredService<ResourceRepository>(),
            Microsoft.Extensions.Options.Options.Create(new ResourcefullOptions { MaxVersions = 1 }));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await keepingOne.GetContainerClient("c").PruneAsync();

        // Assert
        Activity prune = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull prune_container");
        Assert.Equal(2L, prune.GetTagItem("resourcefull.pruned"));
    }

    /// <summary>Refuses to hear about the versions of the name <c>refused</c>.</summary>
    private sealed class RefusingCleanupHandler : IResourceCleanupHandler
    {
        public ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken) =>
            cleanup.Resource.Name == "refused" ? throw new InvalidOperationException("The audit system is down.") : ValueTask.CompletedTask;
    }

    [Fact]
    public async Task PruneAsync_ShouldRecordWhatItPruned_WhenSomeResourcesFail()
    {
        // Arrange: two names with three versions each, then a service keeping one whose cleanup handler refuses one name.
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        foreach (string name in new[] { "kept", "refused" })
        {
            for (int i = 0; i < 3; i++) await container.UploadAsync(name, BinaryData.FromString($"{i}"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        }
        var keepingOne = new ResourceServiceClient(provider.GetRequiredService<ResourceRepository>(),
            Microsoft.Extensions.Options.Options.Create(new ResourcefullOptions { MaxVersions = 1 }),
            cleanupHandler: new RefusingCleanupHandler());
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await Assert.ThrowsAsync<AggregateException>(() => keepingOne.GetContainerClient("c").PruneAsync());

        // Assert
        Activity prune = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull prune_container");
        Assert.Equal(2L, prune.GetTagItem("resourcefull.pruned"));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldRecordNothing_WhenNothingHasExpired()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await service.DeleteExpiredAsync();

        // Assert
        Assert.Empty(SpansIn(root));
    }

    [Fact]
    public async Task ExportAndImport_ShouldRecordHowManyResourcesAndVersionsTheyMoved_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("1"));
        await container.UploadAsync("a", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await container.UploadAsync("b", BinaryData.FromString("3"));
        using Activity root = TestSource.StartActivity("test")!;
        var export = new MemoryStream();

        // Act
        await service.ExportContainerAsync("c", export);
        export.Position = 0;
        await service.ImportContainerAsync("d", export);

        // Assert
        foreach (string operation in new[] { "resourcefull export", "resourcefull import" })
        {
            Activity span = Assert.Single(SpansIn(root), s => s.OperationName == operation);
            Assert.Equal((2L, 3L), ((long)span.GetTagItem("resourcefull.resources")!, (long)span.GetTagItem("resourcefull.versions")!));
        }
        // Fewer names than a page holds, so the export read one.
        Activity exportSpan = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull export");
        Assert.Equal(1, exportSpan.GetTagItem("resourcefull.pages"));
    }

    [Fact]
    public async Task GetInfoAsync_ShouldRecordAGetContainerOperation_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.GetInfoAsync();

        // Assert
        Activity span = Assert.Single(SpansIn(root));
        Assert.Equal(("resourcefull get_container", "c"), (span.OperationName, span.GetTagItem("resourcefull.container")));
    }

    [Fact]
    public async Task GetUsageAsync_ShouldRecordAGetUsageOperation_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.GetUsageAsync();

        // Assert
        Activity span = Assert.Single(SpansIn(root));
        Assert.Equal(("resourcefull get_usage", "c"), (span.OperationName, span.GetTagItem("resourcefull.container")));
    }

    [Fact]
    public async Task GetPolicyAsync_ShouldRecordAGetContainerPolicyOperationAskingTheProvider_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UsePolicyProvider<NoPolicies>()).BuildServiceProvider();
        IResourceContainerClient container =
            await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.GetPolicyAsync();

        // Assert
        Activity span = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull get_container_policy");
        Assert.Equal("c", span.GetTagItem("resourcefull.container"));
        Assert.Single(SpansIn(root),
            s => s.OperationName == "resourcefull get_policy" && s.ParentSpanId == span.SpanId);
    }

    private sealed class NoPolicies : IResourcePolicyProvider
    {
        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName,
            CancellationToken cancellationToken) => ValueTask.FromResult<ResourceContainerPolicy?>(null);
    }

    [Fact]
    public async Task DownloadContentAsync_ShouldRecordOneDownload_WhenCalled()
    {
        // Arrange: it reads through the streaming download, which mustn't record a second one.
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("report.txt", BinaryData.FromString("hello"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.DownloadContentAsync("report.txt");

        // Assert
        Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull download");
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldRecordOneOperation_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        var service = provider.GetRequiredService<IResourceServiceClient>();
        await service.CreateContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await service.DeleteContainerIfExistsAsync("c");

        // Assert
        Assert.Equal(["resourcefull delete_container"], SpansIn(root).Select(s => s.OperationName));
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldRecordTheResourceName_WhenResourceNamesAreOptedIn()
    {
        // Arrange
        using ServiceProvider provider = NewServices(recordResourceNames: true);
        IResourceContainerClient container = provider.GetRequiredService<IResourceServiceClient>().GetContainerClient("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.GetResourceInfoAsync("report.txt");

        // Assert
        Assert.Equal("report.txt", Assert.Single(SpansIn(root)).GetTagItem("resourcefull.resource"));
    }

    [Fact]
    public async Task ContainerOperations_ShouldThrowArgumentNullException_WhenTheNameIsNullAndAListenerIsAttached()
    {
        // Arrange: this class's listener is attached, so every operation starts a span.
        using ServiceProvider provider = NewServices();
        var service = provider.GetRequiredService<IResourceServiceClient>();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateContainerAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateContainerIfNotExistsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ContainerExistsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.DeleteContainerIfExistsAsync(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_ShouldRecordTheFailureMessageOnlyWithResourceNames_WhenTheMessageNamesTheResource(bool recordResourceNames)
    {
        // Arrange: a conflict's message names the resource.
        using ServiceProvider provider = NewServices(recordResourceNames);
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("payroll-jane-doe.pdf", BinaryData.FromString("x"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await Assert.ThrowsAsync<ResourceConflictException>(() => container.UploadAsync("payroll-jane-doe.pdf", BinaryData.FromString("y"),
            new ResourceUploadOptions { Mode = ResourceWriteMode.Create }));

        // Assert
        Activity upload = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull upload");
        IEnumerable<object?> recorded = [upload.StatusDescription, .. upload.TagObjects.Select(t => t.Value),
            .. upload.Events.SelectMany(e => e.Tags.Select(t => t.Value))];
        Assert.Equal(recordResourceNames, recorded.Any(value => value?.ToString()?.Contains("jane-doe") == true));
        Assert.Equal(ActivityStatusCode.Error, upload.Status);
        Assert.Equal(typeof(ResourceConflictException).FullName, upload.GetTagItem("error.type"));
    }

    [Fact]
    public async Task AddResourcefullCheck_ShouldRecordAHealthCheckSpanAndDuration_WhenCalled()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddResourcefull(o => o.UseInMemory());
        services.AddHealthChecks().AddResourcefullCheck();
        await using ServiceProvider provider = services.BuildServiceProvider();
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await provider.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>().CheckHealthAsync();

        // Assert
        Activity check = Assert.Single(SpansIn(root));
        Assert.Equal("resourcefull health_check", check.OperationName);
        Assert.Equal("inmemory", check.GetTagItem("resourcefull.provider"));
        Assert.Contains(_measurements, m => m.Instrument == "resourcefull.client.operation.duration" && Equals(m.Tags["resourcefull.operation"], "health_check"));
    }

    [Fact]
    public async Task ClientOperations_ShouldRecordTheirOwnTags_WhenTheyHaveThem()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        var service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        ResourceInfo version = await container.UploadAsync("report.txt", BinaryData.FromString("x"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.UploadAsync("report.txt", BinaryData.FromString("y"),
            new ResourceUploadOptions { Mode = ResourceWriteMode.Replace, Conditions = new ResourceConditions { IfMatchVersionId = version.VersionId } });
        await container.GetResourcePageAsync(new ResourceListOptions { PageSize = 7 });
        await service.GetContainerPageAsync(new ContainerListOptions { PageSize = 9 });

        // Assert
        Assert.Equal(true, SpansIn(root).Single(s => s.OperationName == "resourcefull upload").GetTagItem("resourcefull.conditional"));
        Assert.Equal(7, SpansIn(root).Single(s => s.OperationName == "resourcefull list_page").GetTagItem("resourcefull.page_size"));
        Assert.Equal(9, SpansIn(root).Single(s => s.OperationName == "resourcefull list_containers").GetTagItem("resourcefull.page_size"));
    }

    [Fact]
    public async Task ClientOperations_ShouldRecordTheStoreName_WhenTheStoreHasOne()
    {
        // Arrange
        await using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory()).AddKeyedResourcefull("telemetry-archive", o => o.UseInMemory())
            .BuildServiceProvider();
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("hot");
        await provider.GetRequiredKeyedService<IResourceServiceClient>("telemetry-archive").CreateContainerAsync("cold");

        // Assert: the store without a name adds no tag, so what it records doesn't change.
        List<Activity> spans = SpansIn(root);
        Assert.Null(spans.Single(s => Equals(s.GetTagItem("resourcefull.container"), "hot")).GetTagItem("resourcefull.store"));
        Assert.Equal("telemetry-archive",
            spans.Single(s => Equals(s.GetTagItem("resourcefull.container"), "cold")).GetTagItem("resourcefull.store"));
        Assert.Contains(_measurements, m => m.Instrument == "resourcefull.client.operation.duration"
            && Equals(m.Tags.GetValueOrDefault("resourcefull.store"), "telemetry-archive"));
    }

    private sealed class FailingCleanupHandler : IResourceCleanupHandler
    {
        public ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The audit log is down.");
    }

    private List<Dictionary<string, object?>> MeasurementsOf(string instrument) =>
        [.. _measurements.Where(m => m.Instrument == instrument).Select(m => m.Tags)];

    [Fact]
    public async Task UploadAsync_ShouldCountTheFailedPruneAndCleanupHandler_WhenTheHandlerThrowsWhilePruning()
    {
        // Arrange: without versioning, an overwrite prunes the version before it.
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseCleanupHandler<FailingCleanupHandler>())
            .BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"));

        // Act
        await container.UploadAsync("a", BinaryData.FromString("y"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert
        Dictionary<string, object?> prune = Assert.Single(MeasurementsOf("resourcefull.client.prune.failures"));
        Assert.Equal(("inmemory", "System.InvalidOperationException"), (prune["resourcefull.provider"], prune["error.type"]));
        Dictionary<string, object?> handler = Assert.Single(MeasurementsOf("resourcefull.client.cleanup_handler.failures"));
        Assert.Equal(("pruned", "System.InvalidOperationException"), (handler["resourcefull.cleanup_reason"], handler["error.type"]));
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldCountAndTagTheRefusal_WhenTheVersionIsHeld()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"), new ResourceUploadOptions { LegalHold = true });
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await Assert.ThrowsAsync<ResourceRetainedException>(() => container.DeleteResourceIfExistsAsync("a"));

        // Assert
        Dictionary<string, object?> refused = Assert.Single(MeasurementsOf("resourcefull.client.delete.refused"));
        Assert.Equal(("inmemory", "delete", "legal_hold"),
            (refused["resourcefull.provider"], refused["resourcefull.operation"], refused["resourcefull.refusal_reason"]));
        Activity delete = SpansIn(root).Single(s => s.OperationName == "resourcefull delete");
        Assert.Equal("legal_hold", delete.GetTagItem("resourcefull.refusal_reason"));
    }

    [Fact]
    public async Task GetResourcePageAsync_ShouldTagTheKeyButNotTheValue_WhenSearchingByMetadata()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseMetadataSearch("customer"))
            .BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"),
            new ResourceUploadOptions { Metadata = new Dictionary<string, string> { ["customer"] = "secret-42" } });
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.GetResourcePageAsync(new ResourceListOptions { Metadata = new ResourceMetadataMatch("customer", "secret-42") });
        long indexed = await service.GetContainerClient("c").IndexMetadataAsync();

        // Assert: the value may be personal data, so no tag carries it.
        Activity page = SpansIn(root).Single(s => s.OperationName == "resourcefull list_page");
        Assert.Equal("customer", page.GetTagItem("resourcefull.metadata_key"));
        Assert.DoesNotContain(SpansIn(root).SelectMany(s => s.Tags), tag => tag.Value == "secret-42");
        Activity index = SpansIn(root).Single(s => s.OperationName == "resourcefull index_metadata");
        Assert.Equal((0L, 0L), (indexed, (long)index.GetTagItem("resourcefull.indexed")!));
    }

    /// <summary>A store where every delete finds its version protected, as when a hold lands just before it.</summary>
    private sealed class RefusingDeletes : ResourceInMemoryRepository
    {
        public override Task<bool> DeleteResourceAsync(ResourceAddress resource, string? versionId = null,
            ResourceConditions? conditions = null, CancellationToken cancellationToken = default) =>
            throw new ResourceRetainedException("Held since the prune read it.", versionId ?? "", null, true);
    }

    [Fact]
    public async Task UploadAsync_ShouldCountThePruneRefusal_WhenTheVersionItPrunesIsHeldBeforeItsDelete()
    {
        // Arrange: without versioning, an overwrite prunes the version before it.
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseProvider(_ => new RefusingDeletes(), Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton))
            .BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"));

        // Act
        await container.UploadAsync("a", BinaryData.FromString("y"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert: the upload succeeds, and the refusal is counted rather than failing the prune.
        Dictionary<string, object?> refused = Assert.Single(MeasurementsOf("resourcefull.client.delete.refused"));
        Assert.Equal(("prune", "legal_hold"), (refused["resourcefull.operation"], refused["resourcefull.refusal_reason"]));
        Assert.Empty(MeasurementsOf("resourcefull.client.prune.failures"));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldCountTheVersionsItDeleted_WhenSomeHaveExpired()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        var expired = new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        await container.UploadAsync("a", BinaryData.FromString("x"), expired);
        await container.UploadAsync("b", BinaryData.FromString("x"), expired);

        // Act
        await service.DeleteExpiredAsync();

        // Assert
        Assert.Equal(2, MeasurementsOf("resourcefull.client.expired").Count(tags => Equals(tags["resourcefull.provider"], "inmemory")));
        Assert.Empty(MeasurementsOf("resourcefull.client.expire.failures"));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldCountTheFailedVersionsAndCleanupHandler_WhenTheHandlerThrows()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseCleanupHandler<FailingCleanupHandler>())
            .BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });

        // Act
        await Assert.ThrowsAsync<AggregateException>(() => service.DeleteExpiredAsync());

        // Assert
        Dictionary<string, object?> failure = Assert.Single(MeasurementsOf("resourcefull.client.expire.failures"));
        Assert.Equal("System.InvalidOperationException", failure["error.type"]);
        Assert.Equal("expired", Assert.Single(MeasurementsOf("resourcefull.client.cleanup_handler.failures"))["resourcefull.cleanup_reason"]);
        Assert.Empty(MeasurementsOf("resourcefull.client.expired"));
    }

    /// <summary>A store whose containers keep deleted versions, and a version in the bin that's due to be purged.</summary>
    private static async Task<(ServiceProvider Provider, IResourceContainerClient Container)> WithDueBinAsync(
        Action<ResourcefullOptionsBuilder>? configure = null)
    {
        ServiceProvider provider = new ServiceCollection().AddResourcefull(o =>
        {
            o.UseInMemory().UseExpiry().UseContainerPolicies(new ResourceContainerPolicy { KeepDeletedFor = TimeSpan.FromDays(1) });
            configure?.Invoke(o);
        }).BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("due", BinaryData.FromString("x"));
        await provider.GetRequiredService<ResourceRepository>().SoftDeleteResourceAsync(new ResourceAddress("c", "due"), null, null,
            DateTimeOffset.UtcNow.AddDays(-1));
        return (provider, container);
    }

    [Fact]
    public async Task PurgeDeletedAsync_ShouldCountTheVersionsPurgedAndUndeleted_WhenCalled()
    {
        // Arrange: besides the due version, one to bring back and one to purge early.
        (ServiceProvider provider, IResourceContainerClient container) = await WithDueBinAsync();
        using ServiceProvider disposal = provider;
        await container.UploadAsync("back", BinaryData.FromString("x"));
        await container.UploadAsync("early", BinaryData.FromString("x"));
        await container.DeleteResourceIfExistsAsync("back");
        await container.DeleteResourceIfExistsAsync("early");

        // Act
        await provider.GetRequiredService<IResourceServiceClient>().PurgeDueAsync();
        await container.UndeleteAsync("back");
        await container.PurgeDeletedAsync("early");

        // Assert
        Assert.Equal(2, MeasurementsOf("resourcefull.client.purged").Count(tags => Equals(tags["resourcefull.provider"], "inmemory")));
        Assert.Single(MeasurementsOf("resourcefull.client.undeleted"));
        Assert.Empty(MeasurementsOf("resourcefull.client.purge.failures"));
    }

    [Fact]
    public async Task PurgeDeletedAsync_ShouldCountTheFailedVersionsAndCleanupHandler_WhenTheHandlerThrows()
    {
        // Arrange
        (ServiceProvider provider, _) = await WithDueBinAsync(o => o.UseCleanupHandler<FailingCleanupHandler>());
        using ServiceProvider disposal = provider;

        // Act
        await Assert.ThrowsAsync<AggregateException>(() => provider.GetRequiredService<IResourceServiceClient>().PurgeDueAsync());

        // Assert
        Dictionary<string, object?> failure = Assert.Single(MeasurementsOf("resourcefull.client.purge.failures"));
        Assert.Equal("System.InvalidOperationException", failure["error.type"]);
        Assert.Equal("purged", Assert.Single(MeasurementsOf("resourcefull.client.cleanup_handler.failures"))["resourcefull.cleanup_reason"]);
        Assert.Empty(MeasurementsOf("resourcefull.client.purged"));
    }

    [Fact]
    public void RetryCountingExecutionStrategy_ShouldCountEachRetry_WhenATransientFailureIsRetried()
    {
        // Arrange: SQL Server's strategy retries a timeout, so no database is needed.
        DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
            .UseSqlServer("Server=unused", sql => sql.ExecutionStrategy(dependencies =>
                new RetryCountingExecutionStrategy(dependencies, maxRetryCount: 2, maxRetryDelay: TimeSpan.Zero, errorNumbersToAdd: [])))
            .Options;
        using var context = new ResourceDbContext(options);
        int attempts = 0;

        // Act
        context.Database.CreateExecutionStrategy().Execute(() => ++attempts < 3 ? throw new TimeoutException() : attempts);

        // Assert
        List<Dictionary<string, object?>> retries = MeasurementsOf("resourcefull.sqlserver.retries");
        Assert.Equal(2, retries.Count);
        Assert.All(retries, tags => Assert.Equal(("sqlserver", "System.TimeoutException"), (tags["resourcefull.provider"], tags["error.type"])));
    }

    /// <summary>Gives a policy once per container, then fails, as a policy store that goes down.</summary>
    private sealed class OncePolicies : IResourcePolicyProvider
    {
        private static readonly ConcurrentDictionary<string, bool> Given = new();

        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            Given.TryAdd(containerName, true)
                ? ValueTask.FromResult<ResourceContainerPolicy?>(new ResourceContainerPolicy())
                : throw new InvalidOperationException("The policy store is down.");
    }

    [Fact]
    public async Task UploadAsync_ShouldCountTheLastKnownPolicyUse_WhenThePolicyProviderFails()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseContainerPolicies(cacheFor: TimeSpan.Zero).UsePolicyProvider<OncePolicies>())
            .BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"));
        _measurements.Clear();
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.UploadAsync("b", BinaryData.FromString("x"));

        // Assert
        // Uncached, the upload looks the policy up for its checks and again for pruning.
        Activity[] lookups = [.. SpansIn(root).Where(s => s.OperationName == "resourcefull get_policy")];
        Assert.Equal(2, lookups.Length);
        Assert.All(lookups, lookup => Assert.Equal(true, lookup.GetTagItem("resourcefull.policy_last_known")));
        IReadOnlyList<Dictionary<string, object?>> uses = MeasurementsOf("resourcefull.client.policy.last_known_uses");
        Assert.Equal(lookups.Length, uses.Count);
        Assert.All(uses, use => Assert.Equal(("inmemory", "System.InvalidOperationException"), (use["resourcefull.provider"], use["error.type"])));
    }
}
