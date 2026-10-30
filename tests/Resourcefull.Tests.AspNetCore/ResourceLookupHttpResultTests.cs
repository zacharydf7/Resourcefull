using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Moq;
using Resourcefull.AspNetCore;

namespace Resourcefull.Tests.AspNetCore;

public class ResourceLookupHttpResultTests
{
    private static async Task<TestApp> StartWithDigitsAsync()
    {
        TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("digits.txt", BinaryData.FromString("0123456789"), new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null) });
        return app;
    }

    private static HttpRequestMessage Get(string path, Action<HttpRequestHeaders>? headers = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        headers?.Invoke(request.Headers);
        return request;
    }

    [Fact]
    public async Task Resource_ShouldSendTheWholeContentAndAcceptRanges_WhenTheRequestHasNoRange()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("digits.txt"))!;

        // Act
        HttpResponseMessage response = await app.Client.GetAsync("/ranged/digits.txt");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0123456789", await response.Content.ReadAsStringAsync());
        Assert.Equal(["bytes"], response.Headers.AcceptRanges);
        Assert.Equal(10, response.Content.Headers.ContentLength);
        Assert.Equal($"\"{stored.ContentHash}\"", response.Headers.ETag!.Tag);
        Assert.Equal("digits.txt", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Null(response.Content.Headers.ContentRange);
    }

    [Theory]
    [InlineData(2L, 4L, "234", 2L, 4L)]
    [InlineData(7L, null, "789", 7L, 9L)]
    [InlineData(null, 3L, "789", 7L, 9L)]     // the last three bytes
    [InlineData(5L, 100L, "56789", 5L, 9L)]   // cut at the end
    [InlineData(0L, long.MaxValue, "0123456789", 0L, 9L)]
    [InlineData(null, 50L, "0123456789", 0L, 9L)]
    public async Task Resource_ShouldSendPartialContent_WhenTheRequestAsksForOneRange(long? from, long? to, string expected, long first, long last)
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged/digits.txt", h => h.Range = new RangeHeaderValue(from, to)));

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal(new ContentRangeHeaderValue(first, last, 10), response.Content.Headers.ContentRange);
        Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(["bytes"], response.Headers.AcceptRanges);
    }

    [Theory]
    [InlineData(10L, null)]
    [InlineData(20L, 30L)]
    [InlineData(null, 0L)]
    public async Task Resource_ShouldAnswerRangeNotSatisfiable_WhenTheRangeHasNoneOfTheContent(long? from, long? to)
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged/digits.txt", h => h.Range = new RangeHeaderValue(from, to)));

        // Assert
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */10", response.Content.Headers.ContentRange!.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Resource_ShouldSendTheWholeContent_WhenTheRequestAsksForSeveralRanges()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();
        var ranges = new RangeHeaderValue();
        ranges.Ranges.Add(new RangeItemHeaderValue(0, 1));
        ranges.Ranges.Add(new RangeItemHeaderValue(4, 5));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged/digits.txt", h => h.Range = ranges));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0123456789", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Resource_ShouldHonorTheRange_WhenIfRangeHoldsTheCurrentETagOrLastModified()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("digits.txt"))!;

        // Act
        HttpResponseMessage byTag = await app.Client.SendAsync(Get("/ranged/digits.txt", h =>
        {
            h.Range = new RangeHeaderValue(0, 2);
            h.IfRange = new RangeConditionHeaderValue($"\"{stored.ContentHash}\"");
        }));
        HttpResponseMessage byDate = await app.Client.SendAsync(Get("/ranged/digits.txt", h =>
        {
            h.Range = new RangeHeaderValue(0, 2);
            h.IfRange = new RangeConditionHeaderValue(stored.LastModified);
        }));

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, byTag.StatusCode);
        Assert.Equal("012", await byTag.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.PartialContent, byDate.StatusCode);
    }

    [Theory]
    [InlineData("\"sha256:other\"")]
    [InlineData("W/\"sha256:other\"")]
    [InlineData("Sat, 01 Jan 2000 00:00:00 GMT")]
    public async Task Resource_ShouldSendTheWholeContent_WhenIfRangeDoesNotHold(string ifRange)
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged/digits.txt", h =>
        {
            h.Range = new RangeHeaderValue(0, 2);
            h.TryAddWithoutValidation("If-Range", ifRange);
        }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0123456789", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Content.Headers.ContentRange);
    }

    [Fact]
    public async Task Resource_ShouldSendTheWholeContent_WhenIfRangeHoldsAWeakTagOfTheCurrentVersion()
    {
        // Arrange: If-Range compares strongly, so a weak tag never holds.
        await using TestApp app = await StartWithDigitsAsync();
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("digits.txt"))!;

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged/digits.txt", h =>
        {
            h.Range = new RangeHeaderValue(0, 2);
            h.TryAddWithoutValidation("If-Range", $"W/\"{stored.ContentHash}\"");
        }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Resource_ShouldAnswerNotModifiedBeforeTheRange_WhenIfNoneMatchHoldsTheETag()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("digits.txt"))!;

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged/digits.txt", h =>
        {
            h.Range = new RangeHeaderValue(0, 2);
            h.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{stored.ContentHash}\""));
        }));

        // Assert
        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Resource_ShouldSendTheHeadersOfTheWholeContent_WhenAHeadRequestHasARange()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged/digits.txt", h => h.Range = new RangeHeaderValue(0, 2), HttpMethod.Head));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, response.Content.Headers.ContentLength);
        Assert.Equal(["bytes"], response.Headers.AcceptRanges);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Resource_ShouldAnswerNotFound_WhenTheResourceDoesNotExist()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        HttpResponseMessage whole = await app.Client.GetAsync("/ranged/missing.txt");
        HttpResponseMessage part = await app.Client.SendAsync(Get("/ranged/missing.txt", h => h.Range = new RangeHeaderValue(0, 2)));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, whole.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, part.StatusCode);
    }

    [Fact]
    public async Task Resource_ShouldSendTheRangeOfThatVersion_WhenGivenAnOlderVersionId()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();
        ResourceInfo older = (await app.Files.GetResourceInfoAsync("digits.txt"))!;
        await app.Files.UploadAsync("digits.txt", BinaryData.FromString("abcdefghij"), new ResourceUploadOptions { Overwrite = true });

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get($"/ranged/digits.txt?versionId={Uri.EscapeDataString(older.VersionId!)}",
            h => h.Range = new RangeHeaderValue(2, 4)));

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("234", await response.Content.ReadAsStringAsync());
        Assert.Equal($"\"{older.ContentHash}\"", response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Resource_ShouldAnswerNotFound_WhenTheVersionDoesNotExist()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        HttpResponseMessage whole = await app.Client.GetAsync("/ranged/digits.txt?versionId=missing");
        HttpResponseMessage part = await app.Client.SendAsync(Get("/ranged/digits.txt?versionId=missing", h => h.Range = new RangeHeaderValue(0, 2)));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, whole.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, part.StatusCode);
    }

    [Fact]
    public async Task Resource_ShouldAnswerNotFound_WhenTheVersionAskedForIsDeletedBeforeItsRangeOpens()
    {
        // Arrange: a suffix range opens the whole version first, to learn its length; by the time its range opens,
        // the version is gone. Unlike the current version, a version asked for isn't replaced by another.
        var container = new Mock<IResourceContainerClient>(MockBehavior.Strict);
        container.Setup(c => c.DownloadStreamingAsync("n", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceDownloadResult(new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "v1" },
                new MemoryStream(System.Text.Encoding.ASCII.GetBytes("0123456789"))) { TotalLength = 10 });
        container.Setup(c => c.DownloadStreamingAsync("n", "v1", new ResourceRange(7, 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceDownloadResult?)null);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=-3";
        context.Response.Body = new MemoryStream();

        // Act
        await TypedResults.Resource(container.Object, "n", "v1").ExecuteAsync(context);

        // Assert
        container.Verify(c => c.DownloadStreamingAsync("n", "v1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Empty(((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task TypedResource_ShouldPresentTheRangeAsTheOptionsSay_WhenGivenOptions()
    {
        // Arrange
        await using TestApp app = await StartWithDigitsAsync();

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Get("/ranged-inline/digits.txt", h => h.Range = new RangeHeaderValue(1, 1)));

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("1", await response.Content.ReadAsStringAsync());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("shown.txt", response.Content.Headers.ContentDisposition.FileName);
    }

    [Fact]
    public async Task Resource_ShouldStartOverWithTheNewCurrentVersion_WhenTheVersionFoundIsDeletedBeforeItsRangeOpens()
    {
        // Arrange: a suffix range opens the whole content first, to learn its length, then the range of that version;
        // the first time, the version is gone by then, as when a newer upload prunes it.
        static ResourceDownloadResult Download(string versionId, string content, ResourceRange? range = null) =>
            new(new ResourceInfo { ContainerName = "c", Name = "n", VersionId = versionId }, new MemoryStream(System.Text.Encoding.ASCII.GetBytes(content)))
            {
                TotalLength = 10,
                Range = range
            };
        var container = new Mock<IResourceContainerClient>(MockBehavior.Strict);
        container.SetupSequence(c => c.DownloadStreamingAsync("n", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Download("v1", "0123456789"))
            .ReturnsAsync(Download("v2", "abcdefghij"));
        container.Setup(c => c.DownloadStreamingAsync("n", "v1", new ResourceRange(7, 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceDownloadResult?)null);
        container.Setup(c => c.DownloadStreamingAsync("n", "v2", new ResourceRange(7, 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Download("v2", "hij", new ResourceRange(7, 3)));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Headers.Range = "bytes=-3";
        context.Response.Body = new MemoryStream();

        // Act
        await Results.Resource(container.Object, "n").ExecuteAsync(context);

        // Assert
        Assert.Equal(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.Equal("bytes 7-9/10", context.Response.Headers.ContentRange.ToString());
        Assert.Equal("hij", System.Text.Encoding.ASCII.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }
}
