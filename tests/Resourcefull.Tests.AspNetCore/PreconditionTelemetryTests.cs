using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>The listener sees every request in the process, so these tests don't run alongside others.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RequestTelemetryCollection
{
    public const string Name = "Request telemetry";
}

[Collection(RequestTelemetryCollection.Name)]
public sealed class PreconditionTelemetryTests
{
    private static async Task<(HttpStatusCode Status, object? Precondition)> SendAsync(TestApp app, HttpRequestMessage request)
    {
        // ASP.NET Core starts a span per request only while something listens to its source.
        var requests = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = requests.Enqueue
        };
        ActivitySource.AddActivityListener(listener);

        HttpResponseMessage response = await app.Client.SendAsync(request);
        return (response.StatusCode, Assert.Single(requests).GetTagItem("resourcefull.precondition"));
    }

    [Fact]
    public async Task Resource_ShouldRecordNotModifiedOnTheRequestSpan_WhenIfNoneMatchHolds()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo stored = await app.Files.UploadAsync("report.txt", BinaryData.FromString("x"));
        var request = new HttpRequestMessage(HttpMethod.Get, "/files/report.txt");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{stored.ContentHash}\""));

        // Act
        (HttpStatusCode status, object? precondition) = await SendAsync(app, request);

        // Assert
        Assert.Equal(HttpStatusCode.NotModified, status);
        Assert.Equal("not_modified", precondition);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldRecordFailedOnTheRequestSpan_WhenIfMatchIsStale()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("report.txt", BinaryData.FromString("x"));
        var request = new HttpRequestMessage(HttpMethod.Put, "/files/report.txt") { Content = new StringContent("y") };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"sha256:stale\""));

        // Act
        (HttpStatusCode status, object? precondition) = await SendAsync(app, request);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, status);
        Assert.Equal("failed", precondition);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldRecordMetOnTheRequestSpan_WhenIfMatchHolds()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo stored = await app.Files.UploadAsync("report.txt", BinaryData.FromString("x"));
        var request = new HttpRequestMessage(HttpMethod.Put, "/files/report.txt") { Content = new StringContent("y") };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{stored.ContentHash}\""));

        // Act
        (HttpStatusCode status, object? precondition) = await SendAsync(app, request);

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("met", precondition);
    }

    [Fact]
    public async Task Resource_ShouldNotTagTheRequestSpan_WhenTheRequestHasNoPreconditions()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("report.txt", BinaryData.FromString("x"));

        // Act
        (HttpStatusCode status, object? precondition) = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, "/files/report.txt"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(precondition);
    }
}
