using System.Net;
using System.Net.Http.Headers;

namespace Resourcefull.Tests.AspNetCore;

public class ResourceHttpResultTests
{
    private static async Task<TestApp> StartWithAsync(string name, string content)
    {
        TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync(name, BinaryData.FromString(content), new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null) });
        return app;
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
