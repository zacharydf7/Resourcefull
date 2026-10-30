using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Resourcefull.AspNetCore;

namespace Resourcefull.Tests.AspNetCore;

public class ResourceHttpResultTests
{
    private static async Task<TestApp> StartWithAsync(string name, string content)
    {
        TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync(name, BinaryData.FromString(content), new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null) });
        return app;
    }

    /// <summary>Sends a download of <paramref name="name"/> with <paramref name="options"/>, outside a server.</summary>
    private static async Task<HttpResponse> SendAsync(TestApp app, string name, ResourceResultOptions options)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await Results.Resource((await app.Files.DownloadStreamingAsync(name))!, options).ExecuteAsync(context);
        return context.Response;
    }

    private static HttpRequestMessage Get(string path, Action<HttpRequestHeaders>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        headers?.Invoke(request.Headers);
        return request;
    }

    [Fact]
    public async Task Resource_ShouldSendTheContentWithItsHeaders_WhenTheRequestHasNoPreconditions()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("2026/10/report.txt", "hello");
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("2026/10/report.txt"))!;

        // Act
        HttpResponseMessage response = await app.Client.GetAsync("/files/2026/10/report.txt");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
        Assert.Equal(5, response.Content.Headers.ContentLength);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{stored.ContentHash}\"", response.Headers.ETag!.Tag);
        Assert.False(response.Headers.ETag.IsWeak);
        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("report.txt", response.Content.Headers.ContentDisposition.FileName);
    }

    [Fact]
    public async Task Resource_ShouldEncodeTheFileName_WhenTheNameIsNotAscii()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("résumé ü.txt", "x");

        // Act
        HttpResponseMessage response = await app.Client.GetAsync("/files/" + Uri.EscapeDataString("résumé ü.txt"));

        // Assert
        Assert.Equal("résumé ü.txt", response.Content.Headers.ContentDisposition!.FileNameStar);
    }

    [Fact]
    public async Task Resource_ShouldOfferTheContentInlineUnderTheGivenName_WhenGivenOptions()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("report.txt", "x");

        // Act
        HttpResponseMessage response = await app.Client.GetAsync("/inline/report.txt");

        // Assert
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("shown.txt", response.Content.Headers.ContentDisposition.FileName);
    }

    [Theory]
    [InlineData("/files/report.txt", false)]
    [InlineData("/files/report.txt", true)]         // 304 Not Modified
    [InlineData("/ranged/report.txt", false)]
    public async Task Resource_ShouldKeepTheFileOutOfSharedCachesAndStopTypeGuessing_WhenCalled(string path, bool notModified)
    {
        // Arrange
        await using TestApp app = await StartWithAsync("report.txt", "x");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get(path, headers =>
        {
            if (notModified) headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }));

        // Assert
        Assert.Equal(notModified ? HttpStatusCode.NotModified : HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoCache);
        Assert.Equal(["nosniff"], response.Headers.GetValues("X-Content-Type-Options"));
    }

    [Fact]
    public async Task Resource_ShouldSendTheGivenCachePolicy_WhenTheOptionsHaveOne()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("report.txt", "x");

        // Act
        HttpResponse response = await SendAsync(app, "report.txt", new ResourceResultOptions { CacheControl = "public, max-age=60" });

        // Assert
        Assert.Equal("public, max-age=60", response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("text/html", null)]
    [InlineData("image/svg+xml", null)]
    [InlineData("image/png;,text/html", null)]
    [InlineData("image/png, text/html", null)]
    [InlineData(null, null)]
    [InlineData("application/xhtml+xml", "application/xml")]     // a +xml type isn't its suffix's type
    public async Task Resource_ShouldOfferTheFileAsADownload_WhenInlineIsAskedForATypeThatsNotSafeToDisplay(string? contentType, string? allowed)
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("page", BinaryData.FromString("<script></script>"), new ResourceUploadOptions { Headers = new ResourceHeaders(contentType, null) });
        var options = allowed is null ? new ResourceResultOptions { Inline = true } : new ResourceResultOptions { Inline = true, InlineContentTypes = [allowed] };

        // Act
        HttpResponse response = await SendAsync(app, "page", options);

        // Assert
        Assert.StartsWith("attachment", response.Headers.ContentDisposition.ToString());
    }

    [Theory]
    [InlineData("image/png", null)]
    [InlineData("video/mp4", null)]
    [InlineData("text/plain; charset=utf-8", null)]
    [InlineData("text/html", "text/html")]          // an application that allows it
    public async Task Resource_ShouldDisplayTheFile_WhenInlineIsAskedForATypeAllowedToDisplay(string contentType, string? allowed)
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("file", BinaryData.FromString("x"), new ResourceUploadOptions { Headers = new ResourceHeaders(contentType, null) });
        var options = allowed is null ? new ResourceResultOptions { Inline = true } : new ResourceResultOptions { Inline = true, InlineContentTypes = [allowed] };

        // Act
        HttpResponse response = await SendAsync(app, "file", options);

        // Assert
        Assert.StartsWith("inline", response.Headers.ContentDisposition.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_ShouldAnswerNotModifiedWithoutTheContent_WhenIfNoneMatchHoldsTheEntityTag(bool weak)
    {
        // Arrange: If-None-Match compares weakly, so a weak tag with the same value matches too.
        await using TestApp app = await StartWithAsync("report.txt", "hello");
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("report.txt"))!;

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/files/report.txt",
            h => h.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{stored.ContentHash}\"", weak))));

        // Assert
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"\"{stored.ContentHash}\"", response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Resource_ShouldSendTheContent_WhenIfNoneMatchHoldsAnotherEntityTag()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("report.txt", "hello");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/files/report.txt",
            h => h.IfNoneMatch.Add(new EntityTagHeaderValue("\"sha256:other\""))));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Resource_ShouldAnswerNotModifiedOrSendTheContent_WhenGivenIfModifiedSince()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("report.txt", "hello");
        DateTimeOffset lastModified = (await app.Client.GetAsync("/files/report.txt")).Content.Headers.LastModified!.Value;

        // Act
        HttpResponseMessage unchanged = await app.Client.SendAsync(Get("/files/report.txt", h => h.IfModifiedSince = lastModified));
        HttpResponseMessage changed = await app.Client.SendAsync(Get("/files/report.txt", h => h.IfModifiedSince = lastModified.AddSeconds(-1)));

        // Assert
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    [Fact]
    public async Task Resource_ShouldAnswerPreconditionFailed_WhenIfMatchHoldsAnotherEntityTag()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("report.txt", "hello");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/files/report.txt",
            h => h.IfMatch.Add(new EntityTagHeaderValue("\"sha256:other\""))));

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Resource_ShouldSendTheHeadersWithoutTheContent_WhenTheRequestIsHead()
    {
        // Arrange
        await using TestApp app = await StartWithAsync("report.txt", "hello");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/files/report.txt"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }
}
