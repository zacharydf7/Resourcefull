using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using Resourcefull.AspNetCore;

namespace Resourcefull.Tests.AspNetCore;

public class UploadFromRequestTests
{
    private static HttpRequestMessage Put(string name, string content, Action<HttpRequestHeaders>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/files/" + name) { Content = new StringContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        headers?.Invoke(request.Headers);
        return request;
    }

    private static EntityTagHeaderValue Tag(ResourceInfo resource) => new($"\"{resource.ContentHash}\"");

    [Fact]
    public async Task UploadFromRequestAsync_ShouldStoreTheBodyAndAnswerCreated_WhenTheNameIsNew()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "hello"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ResourceInfo stored = (await response.Content.ReadFromJsonAsync<ResourceInfo>())!;
        Assert.Equal($"\"{stored.ContentHash}\"", response.Headers.ETag!.Tag);
        Assert.Equal("text/plain", stored.ContentType);
        Assert.Equal(5, stored.ContentLength);
        Assert.Equal("hello", (await app.Files.DownloadContentAsync("report.txt"))!.Content.ToString());
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldAnswerOk_WhenItReplacesTheCurrentVersion()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "two"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("two", (await app.Files.DownloadContentAsync("report.txt"))!.Content.ToString());
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldReplaceAndAnswerOk_WhenTheContainerKeepsNoVersions()
    {
        // Arrange: without versioning, an upload that doesn't overwrite would be create-only; a PUT replaces.
        await using TestApp app = await TestApp.StartAsync(versioning: false);
        await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "two"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("two", (await app.Files.DownloadContentAsync("report.txt"))!.Content.ToString());
        Assert.Single(await app.Files.GetResourceVersionsAsync("report.txt"));
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldStore_WhenTheCurrentVersionIsNoNewerThanIfUnmodifiedSince()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo first = await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "two",
            h => h.IfUnmodifiedSince = first.LastModified.AddMinutes(1)));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldThrowLengthMismatchAndStoreNothing_WhenTheBodyIsShorterThanItsContentLength()
    {
        // Arrange: a body cut short, as by a dropped connection.
        await using TestApp app = await TestApp.StartAsync();
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream("hel"u8.ToArray());
        context.Request.ContentLength = 5;

        // Act
        Task act() => app.Files.UploadFromRequestAsync("report.txt", context.Request);

        // Assert
        await Assert.ThrowsAsync<ResourceContentLengthMismatchException>(act);
        Assert.False(await app.Files.ExistsAsync("report.txt"));
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldStoreTheOptionsWithoutChangingThem_WhenGivenOptions()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream("hello"u8.ToArray());
        context.Request.ContentType = "text/plain";
        var options = new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("application/x-report", null),
            Metadata = new Dictionary<string, string> { ["owner"] = "ops" },
            ExpiresAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        // Act
        await app.Files.UploadFromRequestAsync("report.txt", context.Request, options);

        // Assert
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("report.txt"))!;
        Assert.Equal("application/x-report", stored.ContentType);
        Assert.Equal("ops", stored.Metadata["owner"]);
        Assert.Equal(options.ExpiresAt, stored.ExpiresAt);
        Assert.False(options.Overwrite);
        Assert.False(options.CreateOnly);
        Assert.Null(options.Conditions);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldAnswerPreconditionFailed_WhenTheNameIsCreatedBetweenTheCheckAndTheWrite()
    {
        // Arrange: If-None-Match with a tag is checked against the current version, found missing, so the write is
        // pinned to create-only; another upload creates the name before the write, which then finds it taken.
        var container = new Mock<IResourceContainerClient>();
        container.Setup(c => c.GetResourceInfoAsync("report.txt", null, It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        container.Setup(c => c.UploadAsync("report.txt", It.IsAny<Stream>(), It.Is<ResourceUploadOptions>(o => o.CreateOnly),
            It.IsAny<CancellationToken>())).ThrowsAsync(new ResourceConflictException("Resource with name report.txt already exists."));
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = "\"sha256:other\"";

        // Act
        IResult result = await container.Object.UploadFromRequestAsync("report.txt", context.Request);

        // Assert
        Assert.Equal(StatusCodes.Status412PreconditionFailed, Assert.IsType<ProblemHttpResult>(result).StatusCode);
        container.Verify(c => c.GetResourceInfoAsync("report.txt", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldStoreOrRefuse_WhenIfMatchHoldsTheCurrentOrAStaleEntityTag()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo first = await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage current = await app.Client.SendAsync(Put("report.txt", "two", h => h.IfMatch.Add(Tag(first))));
        HttpResponseMessage stale = await app.Client.SendAsync(Put("report.txt", "three", h => h.IfMatch.Add(Tag(first))));

        // Assert
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        ResourceInfo second = (await app.Files.GetResourceInfoAsync("report.txt"))!;
        Assert.Equal(Tag(second).Tag, stale.Headers.ETag!.Tag);
        Assert.Equal("two", (await app.Files.DownloadContentAsync("report.txt"))!.Content.ToString());
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldRefuse_WhenIfMatchHoldsOnlyAWeakTag()
    {
        // Arrange: If-Match compares strongly, so a weak tag never matches.
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo first = await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "two",
            h => h.IfMatch.Add(new EntityTagHeaderValue($"\"{first.ContentHash}\"", isWeak: true))));

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldStore_WhenIfMatchListsTheCurrentEntityTagAmongOthers()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo first = await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "two", h =>
        {
            h.IfMatch.Add(new EntityTagHeaderValue("\"sha256:other\""));
            h.IfMatch.Add(Tag(first));
        }));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldStoreOnlyIfTheNameExists_WhenIfMatchIsAny()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("existing.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage existing = await app.Client.SendAsync(Put("existing.txt", "two", h => h.IfMatch.Add(EntityTagHeaderValue.Any)));
        HttpResponseMessage missing = await app.Client.SendAsync(Put("missing.txt", "two", h => h.IfMatch.Add(EntityTagHeaderValue.Any)));

        // Assert
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, missing.StatusCode);
        Assert.False(await app.Files.ExistsAsync("missing.txt"));
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldStoreOnlyIfTheNameIsNew_WhenIfNoneMatchIsAny()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("existing.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage created = await app.Client.SendAsync(Put("new.txt", "x", h => h.IfNoneMatch.Add(EntityTagHeaderValue.Any)));
        HttpResponseMessage refused = await app.Client.SendAsync(Put("existing.txt", "x", h => h.IfNoneMatch.Add(EntityTagHeaderValue.Any)));

        // Assert
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        Assert.Equal("one", (await app.Files.DownloadContentAsync("existing.txt"))!.Content.ToString());
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldRefuse_WhenIfNoneMatchHoldsTheCurrentEntityTag()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo first = await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "two", h => h.IfNoneMatch.Add(Tag(first))));

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldRefuse_WhenTheCurrentVersionIsNewerThanIfUnmodifiedSince()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo first = await app.Files.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("report.txt", "two",
            h => h.IfUnmodifiedSince = first.LastModified.AddMinutes(-1)));

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task UploadFromRequestAsync_ShouldAnswerBadRequest_WhenTheNameBreaksTheRules()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Put("%20padded", "x"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
}
