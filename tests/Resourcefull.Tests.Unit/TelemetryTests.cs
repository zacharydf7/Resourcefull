using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.InMemory;

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
        // Arrange: a container with a version in it, so every operation has something to work on, and an expired version
        // in another container, which the operations on "c" leave for the expiry sweep to find.
        using ServiceProvider provider = NewServices();
        var service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateResourceContainerAsync("c");
        ResourceInfo version = await container.UploadAsync("r", BinaryData.FromString("x"));
        await (await service.CreateResourceContainerAsync("expiring")).UploadAsync("r", BinaryData.FromString("x"),
            new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
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
                if (result is IAsyncEnumerable<ResourceInfo> listing)
                    await foreach (ResourceInfo _ in listing) { }
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
        foreach (MethodInfo method in typeof(IResourceServiceClient).GetMethods().Where(m => m.Name != nameof(IResourceServiceClient.GetResourceContainerClient)))
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
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
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
    public async Task UploadAsync_ShouldRecordTheErrorType_WhenItFails()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient missing = provider.GetRequiredService<IResourceServiceClient>().GetResourceContainerClient("missing");
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

    [Fact]
    public async Task DownloadStreamingAsync_ShouldCountTheBytesAsTheyAreRead_WhenTheStreamIsRead()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
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
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
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
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
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
        IResourceContainerClient container = await service.CreateResourceContainerAsync("c");
        await service.CreateResourceContainerAsync("d");
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
    public async Task DeleteExpiredAsync_ShouldRecordHowManyVersionsItDeleted_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateResourceContainerAsync("c");
        ResourceInfo stored = await container.UploadAsync("a", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        await container.UploadAsync("b", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.SetExpiryAsync("a", stored.VersionId!, DateTimeOffset.UtcNow.AddDays(1));
        await service.DeleteExpiredAsync();

        // Assert
        Activity expire = Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull expire");
        Assert.Equal(1, expire.GetTagItem("resourcefull.expired"));
        Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull set_expiry");
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
    public async Task GetUsageAsync_ShouldRecordAGetUsageOperation_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.GetUsageAsync();

        // Assert
        Activity span = Assert.Single(SpansIn(root));
        Assert.Equal(("resourcefull get_usage", "c"), (span.OperationName, span.GetTagItem("resourcefull.container")));
    }

    [Fact]
    public async Task DownloadContentAsync_ShouldRecordOneDownload_WhenCalled()
    {
        // Arrange: it reads through the streaming download, which mustn't record a second one.
        using ServiceProvider provider = NewServices();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
        await container.UploadAsync("report.txt", BinaryData.FromString("hello"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.DownloadContentAsync("report.txt");

        // Assert
        Assert.Single(SpansIn(root), s => s.OperationName == "resourcefull download");
    }

    [Fact]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldRecordOneOperation_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = NewServices();
        var service = provider.GetRequiredService<IResourceServiceClient>();
        await service.CreateResourceContainerAsync("c");
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await service.DeleteResourceContainerIfExistsAsync("c");

        // Assert
        Assert.Equal(["resourcefull delete_container"], SpansIn(root).Select(s => s.OperationName));
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldRecordTheResourceName_WhenResourceNamesAreOptedIn()
    {
        // Arrange
        using ServiceProvider provider = NewServices(recordResourceNames: true);
        IResourceContainerClient container = provider.GetRequiredService<IResourceServiceClient>().GetResourceContainerClient("c");
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
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateResourceContainerAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CreateResourceContainerIfNotExistsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.GetResourceContainerInfoAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ContainerExistsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.DeleteResourceContainerIfExistsAsync(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_ShouldRecordTheFailureMessageOnlyWithResourceNames_WhenTheMessageNamesTheResource(bool recordResourceNames)
    {
        // Arrange: a conflict's message names the resource.
        using ServiceProvider provider = NewServices(recordResourceNames);
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
        await container.UploadAsync("payroll-jane-doe.pdf", BinaryData.FromString("x"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await Assert.ThrowsAsync<ResourceConflictException>(() => container.UploadAsync("payroll-jane-doe.pdf", BinaryData.FromString("y"),
            new ResourceUploadOptions { CreateOnly = true }));

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
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
        ResourceInfo version = await container.UploadAsync("report.txt", BinaryData.FromString("x"));
        using Activity root = TestSource.StartActivity("test")!;

        // Act
        await container.UploadAsync("report.txt", BinaryData.FromString("y"),
            new ResourceUploadOptions { Conditions = new ResourceConditions { IfMatchVersionId = version.VersionId } });
        await container.GetResourcePageAsync(new ResourceListOptions { PageSize = 7 });

        // Assert
        Assert.Equal(true, SpansIn(root).Single(s => s.OperationName == "resourcefull upload").GetTagItem("resourcefull.conditional"));
        Assert.Equal(7, SpansIn(root).Single(s => s.OperationName == "resourcefull list_page").GetTagItem("resourcefull.page_size"));
    }
}
