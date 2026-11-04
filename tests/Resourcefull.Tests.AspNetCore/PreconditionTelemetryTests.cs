using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Resourcefull.AspNetCore;
using Resourcefull.Testing;

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
    private static Task<(HttpStatusCode Status, object? Tag)> SendAsync(TestApp app, HttpRequestMessage request,
        string tag = "resourcefull.precondition") => SendAsync(app.Client, request, tag);

    private static async Task<(HttpStatusCode Status, object? Tag)> SendAsync(HttpClient client, HttpRequestMessage request, string tag)
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

        HttpResponseMessage response = await client.SendAsync(request);
        // The test server hands back the response before the request finishes and its span stops.
        Assert.True(SpinWait.SpinUntil(() => !requests.IsEmpty, TimeSpan.FromSeconds(5)), "The request's span never stopped.");
        return (response.StatusCode, Assert.Single(requests).GetTagItem(tag));
    }

    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("expired", HttpStatusCode.Gone)]
    [InlineData("invalid", HttpStatusCode.NotFound)]
    public async Task SharedResource_ShouldRecordTheLinksStatusOnTheRequestSpan_WhenALinkIsOpened(string expected, HttpStatusCode expectedStatus)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using TestApp app = await TestApp.StartAsync(clock: clock);
        await app.Files.UploadAsync("report.txt", BinaryData.FromString("x"));
        string token = expected == "invalid" ? "not-a-token"
            : (await app.Links.CreateTokenAsync(new ResourceLink(TestApp.Container, "report.txt")))!;
        if (expected == "expired") clock.Now = clock.Now.AddHours(2);

        // Act
        (HttpStatusCode status, object? link) = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, $"/shared/{token}"), "resourcefull.link");

        // Assert
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expected, link);
    }

    [Theory]
    [InlineData("valid", HttpStatusCode.Created)]
    [InlineData("expired", HttpStatusCode.Gone)]
    [InlineData("invalid", HttpStatusCode.NotFound)]
    public async Task SharedUpload_ShouldRecordTheLinksStatusOnTheRequestSpan_WhenALinkIsUsed(string expected, HttpStatusCode expectedStatus)
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using TestApp app = await TestApp.StartAsync(clock: clock);
        string token = expected == "invalid" ? "not-a-token"
            : app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { ResourceName = "report.txt" });
        if (expected == "expired") clock.Now = clock.Now.AddHours(2);

        // Act
        (HttpStatusCode status, object? link) = await SendAsync(app,
            new HttpRequestMessage(HttpMethod.Put, $"/upload/{token}") { Content = new StringContent("x") }, "resourcefull.link");

        // Assert
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expected, link);
    }

    [Theory]
    [InlineData(true, "archive")]
    [InlineData(false, null)]
    public async Task SharedResource_ShouldRecordANamedStoreOnTheRequestSpan_WhenItsLinkIsOpened(bool archive, string? expected)
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();


        // Act
        (_, object? store) = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, (archive ? "/archive/shared/" : "/shared/") + "not-a-token"),
            "resourcefull.store");

        // Assert
        Assert.Equal(expected, store);
    }

    [Theory]
    [InlineData("options", HttpStatusCode.NoContent)]
    [InlineData("create", HttpStatusCode.Created)]
    [InlineData("resume", HttpStatusCode.OK)]
    [InlineData("append", HttpStatusCode.NoContent)]
    [InlineData("complete", HttpStatusCode.NoContent)]
    [InlineData("cancel", HttpStatusCode.NoContent)]
    public async Task MapResourcefullUploads_ShouldRecordWhatTheRequestDidOnItsSpan_WhenCalled(string expected, HttpStatusCode expectedStatus)
    {
        // Arrange: an upload of 4 bytes, with 2 stored.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = (await app.Client.SendAsync(TusRequests.Create(4, TusRequests.Metadata(("filename", "a.bin"))))).Headers.Location!.ToString();
        await app.Client.SendAsync(TusRequests.Patch(url, 0, "ab"u8.ToArray()));
        HttpRequestMessage request = expected switch
        {
            "options" => new HttpRequestMessage(HttpMethod.Options, "/uploads"),
            "create" => TusRequests.Create(4, TusRequests.Metadata(("filename", "b.bin"))),
            "resume" => TusRequests.Tus(HttpMethod.Head, url),
            "append" => TusRequests.Patch(url, 2, "c"u8.ToArray()),
            "complete" => TusRequests.Patch(url, 2, "cd"u8.ToArray()),
            _ => TusRequests.Tus(HttpMethod.Delete, url)
        };

        // Act
        (HttpStatusCode status, object? tus) = await SendAsync(app.Client, request, "resourcefull.tus");

        // Assert
        Assert.Equal(expectedStatus, status);
        Assert.Equal(expected, tus);
    }

    [Theory]
    [InlineData("/archive-uploads", "archive")]
    [InlineData("/uploads", null)]
    public async Task MapResourcefullUploads_ShouldRecordANamedStoreOnTheRequestSpan_WhenItsEndpointsAreCalled(string path, string? expected)
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();

        // Act
        (_, object? store) = await SendAsync(app.Client, new HttpRequestMessage(HttpMethod.Options, path), "resourcefull.store");

        // Assert
        Assert.Equal(expected, store);
    }

    [Theory]
    [InlineData("/resourcefull/", "page")]
    [InlineData("/resourcefull/api/health", "health")]
    [InlineData("/resourcefull/api/containers", "containers")]
    [InlineData("/resourcefull/api/container?name=files", "container")]
    [InlineData("/resourcefull/api/resources?container=files", "resources")]
    public async Task MapResourcefullDashboard_ShouldRecordTheEndpointOnTheRequestSpan_WhenCalled(string path, string expected)
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        (HttpStatusCode status, object? endpoint) = await SendAsync(app, new HttpRequestMessage(HttpMethod.Get, path), "resourcefull.dashboard");

        // Assert
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, endpoint);
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
