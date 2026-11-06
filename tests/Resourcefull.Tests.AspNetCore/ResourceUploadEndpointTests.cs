using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AspNetCore;
using static Resourcefull.Tests.AspNetCore.TusRequests;

namespace Resourcefull.Tests.AspNetCore;

public class ResourceUploadEndpointTests
{
    private static Task<HttpResponseMessage> CreateAsync(UploadApp app, long length, string metadata, string path = "/uploads",
        string? user = null, byte[]? content = null) =>
        app.Client.SendAsync(TusRequests.Create(length, metadata, path, user, content));

    /// <summary>Starts an upload of <paramref name="length"/> bytes named <paramref name="filename"/>, and returns its URL.</summary>
    private static async Task<string> StartAsync(UploadApp app, long length, string filename = "a.bin", string path = "/uploads",
        string? user = null, params (string Key, string? Value)[] more)
    {
        HttpResponseMessage created = await CreateAsync(app, length, Metadata([("filename", filename), .. more]), path, user);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return created.Headers.Location!.ToString();
    }

    private static Task<HttpResponseMessage> PatchAsync(UploadApp app, string url, long offset, byte[] bytes, string? user = null) =>
        app.Client.SendAsync(TusRequests.Patch(url, offset, bytes, user));

    private static Task<HttpResponseMessage> HeadAsync(UploadApp app, string url, string? user = null) =>
        app.Client.SendAsync(Tus(HttpMethod.Head, url, user));

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.Single() : null;

    private static async Task<string?> ReadAsync(IResourceContainerClient container, string name) =>
        (await container.DownloadContentAsync(name))?.Content.ToString();

    [Fact]
    public async Task Options_ShouldAdvertiseTheVersionExtensionsAndMaxSize_WhenCalled()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync(maxSize: 1000);

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/uploads"));

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(("1.0.0", "1.0.0", "1000"), (Header(response, "Tus-Version"), Header(response, "Tus-Resumable"), Header(response, "Tus-Max-Size")));
        Assert.Equal("creation,creation-with-upload,termination,expiration", Header(response, "Tus-Extension"));
    }

    [Fact]
    public async Task Post_ShouldAnswerPreconditionFailedWithTheVersion_WhenTusResumableIsMissing()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/uploads");
        request.Headers.Add("Upload-Length", "5");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("1.0.0", Header(response, "Tus-Version"));
    }

    [Fact]
    public async Task Upload_ShouldStoreTheFileAsItsNamesCurrentVersion_WhenSentInChunks()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 11, "report.txt", more: ("filetype", "text/plain"));

        // Act
        HttpResponseMessage first = await PatchAsync(app, url, 0, "hello "u8.ToArray());
        HttpResponseMessage last = await PatchAsync(app, url, 6, "world"u8.ToArray());

        // Assert: each answer says how much is stored, and the file is stored, with its type, and passed to the app once.
        Assert.Equal((HttpStatusCode.NoContent, "6"), (first.StatusCode, Header(first, "Upload-Offset")));
        Assert.Equal((HttpStatusCode.NoContent, "11"), (last.StatusCode, Header(last, "Upload-Offset")));
        Assert.Equal("hello world", await ReadAsync(app.Files, "tus/report.txt"));
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("tus/report.txt"))!;
        Assert.Equal("text/plain", stored.ContentType);
        Assert.Equal(stored.VersionId, Assert.Single(app.Completed).VersionId);
    }

    [Fact]
    public async Task Head_ShouldReportWhatsStoredWithoutCaching_WhenTheUploadIsUnfinished()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 10);
        await PatchAsync(app, url, 0, "abcd"u8.ToArray());

        // Act
        HttpResponseMessage response = await HeadAsync(app, url);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("4", "10"), (Header(response, "Upload-Offset"), Header(response, "Upload-Length")));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Head_ShouldReportTheWholeLength_WhenTheUploadIsComplete()
    {
        // Arrange: a client that resumes an upload it finished, as one does after the page is reloaded.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3);
        await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Act
        HttpResponseMessage response = await HeadAsync(app, url);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("3", "3"), (Header(response, "Upload-Offset"), Header(response, "Upload-Length")));
    }

    [Fact]
    public async Task Patch_ShouldAnswerConflictAndStoreNothing_WhenTheOffsetIsWrong()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 10);
        await PatchAsync(app, url, 0, "abcd"u8.ToArray());

        // Act
        HttpResponseMessage response = await PatchAsync(app, url, 2, "zz"u8.ToArray());

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("4", Header(await HeadAsync(app, url), "Upload-Offset"));
    }

    [Fact]
    public async Task Patch_ShouldAnswerUnsupportedMediaType_WhenTheContentIsntTusContent()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 4);
        HttpRequestMessage request = Tus(HttpMethod.Patch, url);
        request.Headers.Add("Upload-Offset", "0");
        request.Content = new ByteArrayContent("abcd"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } };

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("0", Header(await HeadAsync(app, url), "Upload-Offset"));
    }

    [Theory]
    [InlineData(null, "filename YQ==", null)]
    [InlineData("five", "filename YQ==", null)]
    [InlineData("-1", "filename YQ==", null)]
    [InlineData(null, "filename YQ==", "1")]
    [InlineData("5", "filename not-base64!", null)]
    [InlineData("5", "filename YQ==,filename Yg==", null)]
    [InlineData("5", "filename YQ== extra", null)]
    public async Task Post_ShouldAnswerBadRequest_WhenGivenAHeaderItCantUse(string? length, string metadata, string? deferLength)
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        HttpRequestMessage request = Tus(HttpMethod.Post, "/uploads");
        if (length is not null) request.Headers.Add("Upload-Length", length);
        if (deferLength is not null) request.Headers.Add("Upload-Defer-Length", deferLength);
        request.Headers.Add("Upload-Metadata", metadata);

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Post_ShouldAnswerForbidden_WhenTheAppRefusesTheUpload()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();

        // Act
        HttpResponseMessage response = await CreateAsync(app, 5, Metadata(("filename", "a.bin"), ("refuse", null)));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Post_ShouldAnswerConflictBeforeAnythingIsSent_WhenTheNameExistsAndTheUploadCreates()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        await app.Files.UploadAsync("tus/a.bin", BinaryData.FromString("old"));

        // Act
        HttpResponseMessage response = await CreateAsync(app, 5, Metadata(("filename", "a.bin")));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Upload_ShouldReplaceTheCurrentVersion_WhenTheAppSaysItReplaces()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        await app.Files.UploadAsync("tus/a.bin", BinaryData.FromString("old"));
        string url = await StartAsync(app, 3, more: ("replace", null));

        // Act
        HttpResponseMessage response = await PatchAsync(app, url, 0, "new"u8.ToArray());

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("new", await ReadAsync(app.Files, "tus/a.bin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Post_ShouldAnswerContentTooLargeBeforeAnythingIsSent_WhenTheUploadIsOverALimit(bool endpointLimit)
    {
        // Arrange: a limit of 10 bytes on the endpoints, or on the container.
        await using UploadApp app = endpointLimit
            ? await UploadApp.StartAsync(maxSize: 10)
            : await UploadApp.StartAsync(policy: new ContainerPolicy { MaxResourceSize = 10 });

        // Act
        HttpResponseMessage response = await CreateAsync(app, 11, Metadata(("filename", "a.bin")));

        // Assert
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task UploadEndpoints_ShouldAdvertiseBytesAndNameTheLimitWhenRefusing_WhenGivenALimitInUnits()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync(maxSize: ResourceSize.Kilobytes(1));

        // Act
        HttpResponseMessage options = await app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/uploads"));
        HttpResponseMessage atLimit = await CreateAsync(app, 1024, Metadata(("filename", "fits.bin")));
        HttpResponseMessage over = await CreateAsync(app, 1025, Metadata(("filename", "big.bin")));

        // Assert: the protocol's header is a number of bytes, and the refusal gives the limit in units.
        Assert.Equal("1024", Header(options, "Tus-Max-Size"));
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, over.StatusCode);
        Assert.Contains("larger than the 1 KB these endpoints take", await over.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_ShouldCancelTheUploadAndAnswerConflict_WhenTheNameIsTakenBeforeTheUploadCompletes()
    {
        // Arrange: an upload that creates, and the name taken while it's sent.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3);
        await app.Files.UploadAsync("tus/a.bin", BinaryData.FromString("theirs"));

        // Act
        HttpResponseMessage response = await PatchAsync(app, url, 0, "abc"u8.ToArray());
        HttpResponseMessage resumed = await HeadAsync(app, url);

        // Assert: a client that retries is told the upload is gone, not that it finished.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resumed.StatusCode);
        Assert.Null(Header(resumed, "Upload-Offset"));
        Assert.Equal("theirs", await ReadAsync(app.Files, "tus/a.bin"));
        Assert.Empty(app.Completed);
    }

    [Fact]
    public async Task Head_ShouldCompleteTheUpload_WhenItsCompletionFailedBefore()
    {
        // Arrange: the first completion fails, as when the store can't be reached.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3, path: "/flaky-uploads");
        app.FailCompletions = 1;
        HttpResponseMessage failed = await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Act
        HttpResponseMessage resumed = await HeadAsync(app, url);

        // Assert: the client is told to retry, then that the upload finished, once it's stored.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal((HttpStatusCode.OK, "3"), (resumed.StatusCode, Header(resumed, "Upload-Offset")));
        Assert.Equal("abc", await ReadAsync(app.Files, "tus/a.bin"));
        Assert.Single(app.Completed);
    }

    [Fact]
    public async Task Head_ShouldAnswerLockedRatherThanTheWholeLength_WhenCompletingStillFails()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3, path: "/flaky-uploads");
        app.FailCompletions = 2;
        await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Act
        HttpResponseMessage resumed = await HeadAsync(app, url);

        // Assert: locked, which tus clients retry, where they'd start again from the beginning after another error.
        Assert.Equal(HttpStatusCode.Locked, resumed.StatusCode);
        Assert.Equal("1", Header(resumed, "Retry-After"));
        Assert.Null(Header(resumed, "Upload-Offset"));
        Assert.Null(await app.Files.GetResourceInfoAsync("tus/a.bin"));
        Assert.Contains(app.Logs.Messages, m => m.Category == "Resourcefull.AspNetCore.Uploads" && m.Message.Contains("kept"));
    }

    [Fact]
    public async Task Patch_ShouldKeepTheVersionAndAnswerServerError_WhenTheAppsCallbackThrows()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3, more: ("fail-callback", null));

        // Act
        HttpResponseMessage response = await PatchAsync(app, url, 0, "abc"u8.ToArray());
        HttpResponseMessage resumed = await HeadAsync(app, url);

        // Assert: the version stays, the failure is logged, and a client that resumes is told the upload finished.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("abc", await ReadAsync(app.Files, "tus/a.bin"));
        Assert.Contains(app.Logs.Messages, m => m.Category == "Resourcefull.AspNetCore.Uploads" && m.Message.Contains("OnCompletedAsync"));
        Assert.Equal((HttpStatusCode.OK, "3"), (resumed.StatusCode, Header(resumed, "Upload-Offset")));
    }

    [Fact]
    public async Task Head_ShouldReportTheWholeLength_WhenItCompletesTheUploadAndTheAppsCallbackThrows()
    {
        // Arrange: the last request's completion fails, then the app's callback throws when the client resumes.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3, path: "/flaky-uploads", more: ("fail-callback", null));
        app.FailCompletions = 1;
        await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Act
        HttpResponseMessage resumed = await HeadAsync(app, url);

        // Assert: the file is stored, so the client is told it finished rather than an error, which would make it start again.
        Assert.Equal((HttpStatusCode.OK, "3"), (resumed.StatusCode, Header(resumed, "Upload-Offset")));
        Assert.Equal("abc", await ReadAsync(app.Files, "tus/a.bin"));
        Assert.Contains(app.Logs.Messages, m => m.Category == "Resourcefull.AspNetCore.Uploads" && m.Message.Contains("OnCompletedAsync"));
    }

    [Fact]
    public async Task Post_ShouldCancelTheUploadAndAnswerContentTooLarge_WhenItCarriesMoreThanTheLengthWithoutSayingSo()
    {
        // Arrange: content longer than the upload, with no Content-Length.
        await using UploadApp app = await UploadApp.StartAsync();
        HttpRequestMessage request = Create(3, Metadata(("filename", "a.bin")), "/flaky-uploads");
        request.Content = new StreamContent(new UnknownLengthStream("abcd"u8.ToArray()));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert: refused, with no URL to resume, and the upload it started canceled.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(1, app.Cancellations);
    }

    [Fact]
    public async Task Delete_ShouldCancelTheUpload_WhenCalled()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 10);
        await PatchAsync(app, url, 0, "abcd"u8.ToArray());

        // Act
        HttpResponseMessage canceled = await app.Client.SendAsync(Tus(HttpMethod.Delete, url));
        HttpResponseMessage again = await app.Client.SendAsync(Tus(HttpMethod.Delete, url));

        // Assert
        Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (canceled.StatusCode, again.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, (await HeadAsync(app, url)).StatusCode);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("another user")]
    [InlineData("not signed in")]
    public async Task Requests_ShouldAnswerNotFound_WhenTheUrlIsChangedOrAnotherUsers(string variant)
    {
        // Arrange: an upload started by a signed-in user.
        await using UploadApp app = await UploadApp.StartAsync();
        string original = await StartAsync(app, 10, user: "maria");
        string? user = variant == "another user" ? "sam" : variant == "changed" ? "maria" : null;
        string url = variant == "changed" ? original[..^8] + (original[^8] == 'A' ? 'B' : 'A') + original[^7..] : original;

        // Act
        HttpResponseMessage resumed = await HeadAsync(app, url, user);
        HttpResponseMessage appended = await PatchAsync(app, url, 0, "abcd"u8.ToArray(), user);

        // Assert
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (resumed.StatusCode, appended.StatusCode));
        Assert.Equal("0", Header(await HeadAsync(app, original, "maria"), "Upload-Offset"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Patch_ShouldAnswerContentTooLargeAndStoreNoMoreThanTheLength_WhenSentMoreThanTheUploadHasLeft(bool declared)
    {
        // Arrange: an upload of 10 bytes with 4 stored, and 7 more sent, with their length declared, or not.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 10);
        await PatchAsync(app, url, 0, "abcd"u8.ToArray());
        HttpRequestMessage request = Patch(url, 4, "efghijk"u8.ToArray());
        if (!declared)
        {
            request.Content = new StreamContent(new UnknownLengthStream("efghijk"u8.ToArray()));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        }

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert: refused, with nothing past the upload's length stored, before reading anything when the length was
        // declared.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        long offset = long.Parse(Header(await HeadAsync(app, url), "Upload-Offset")!);
        if (declared) Assert.Equal(4, offset);
        else Assert.InRange(offset, 4, 10);
    }

    [Fact]
    public async Task Post_ShouldAnswerContentTooLarge_WhenItCarriesMoreThanTheLength()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();

        // Act
        HttpResponseMessage response = await CreateAsync(app, 3, Metadata(("filename", "a.bin")), content: "abcd"u8.ToArray());

        // Assert
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Requests_ShouldAnswerNotFound_WhenTheUrlIsFromOtherEndpointsOnTheSameStore()
    {
        // Arrange: /uploads and /other-uploads are mapped with different patterns on the same store.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3);

        // Act
        HttpResponseMessage response = await PatchAsync(app, url.Replace("/uploads/", "/other-uploads/"), 0, "abc"u8.ToArray());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("0", Header(await HeadAsync(app, url), "Upload-Offset"));
    }

    [Theory]
    [InlineData(3, "3")]
    [InlineData(10, "3")]
    public async Task Post_ShouldStoreTheContentItCarries_WhenSentWithTheRequestThatStartsTheUpload(long length, string offset)
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();

        // Act
        HttpResponseMessage response = await CreateAsync(app, length, Metadata(("filename", "a.bin")), content: "abc"u8.ToArray());

        // Assert: the content is stored, and the upload completed when that's all of it.
        Assert.Equal((HttpStatusCode.Created, offset), (response.StatusCode, Header(response, "Upload-Offset")));
        Assert.Equal(length == 3 ? "abc" : null, await ReadAsync(app.Files, "tus/a.bin"));
        Assert.Equal(offset, Header(await HeadAsync(app, response.Headers.Location!.ToString()), "Upload-Offset"));
    }

    [Fact]
    public async Task Post_ShouldStoreAnEmptyFile_WhenTheLengthIsZero()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();

        // Act
        HttpResponseMessage response = await CreateAsync(app, 0, Metadata(("filename", "empty.bin")));

        // Assert
        Assert.Equal((HttpStatusCode.Created, "0"), (response.StatusCode, Header(response, "Upload-Offset")));
        Assert.Equal(0, (await app.Files.GetResourceInfoAsync("tus/empty.bin"))!.ContentLength);
    }

    [Fact]
    public async Task Patch_ShouldAppend_WhenSentAsAPostThatOverridesItsMethod()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3);
        HttpRequestMessage request = Tus(HttpMethod.Post, url);
        request.Headers.Add("X-HTTP-Method-Override", "PATCH");
        request.Headers.Add("Upload-Offset", "0");
        request.Content = Content("abc"u8.ToArray());

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("abc", await ReadAsync(app.Files, "tus/a.bin"));
    }

    [Fact]
    public async Task Upload_ShouldStoreInTheNamedStoreOnly_WhenItsEndpointsAreForIt()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, 3, path: "/archive-uploads");

        // Act
        HttpResponseMessage elsewhere = await PatchAsync(app, url.Replace("/archive-uploads/", "/uploads/"), 0, "abc"u8.ToArray());
        HttpResponseMessage response = await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Assert: its URL doesn't work at the other store's endpoints, and the file is in its store only.
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("abc", await ReadAsync(app.ArchiveFiles, "tus/a.bin"));
        Assert.Null(await app.Files.GetResourceInfoAsync("tus/a.bin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Upload_ShouldSayWhenItExpiresOrNot_WhenTheExpirySweepRunsOrNot(bool expiry)
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync(expiry: expiry);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        // Act
        HttpResponseMessage created = await CreateAsync(app, 10, Metadata(("filename", "a.bin")));
        HttpResponseMessage appended = await PatchAsync(app, created.Headers.Location!.ToString(), 0, "abcd"u8.ToArray());

        // Assert: a day after each, when the sweep runs, and nothing when it doesn't.
        foreach (HttpResponseMessage response in new[] { created, appended })
        {
            string? expires = Header(response, "Upload-Expires");
            if (!expiry)
            {
                Assert.Null(expires);
                continue;
            }
            TimeSpan after = DateTimeOffset.Parse(expires!) - before;
            Assert.InRange(after, TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public void MapResourcefullUploads_ShouldThrow_WhenResolveIsntSetOrDataProtectionIsMissing()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        using WebApplication app = builder.Build();

        // Act
        void noResolve() => app.MapResourcefullUploads("/uploads", _ => { });
        void noDataProtection() => app.MapResourcefullUploads("/uploads", o => o.ResolveAsync = (_, _) => Task.FromResult<ResourceUploadTarget?>(null));

        // Assert
        Assert.Contains("ResolveAsync", Assert.Throws<InvalidOperationException>(noResolve).Message);
        Assert.Contains("AddDataProtection", Assert.Throws<InvalidOperationException>(noDataProtection).Message);
    }

    /// <summary>A stream whose length can't be known in advance, so a request sends it without a Content-Length.</summary>
    private sealed class UnknownLengthStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public void WithResourcefullUploadHeaders_ShouldAllowAndExposeWhatTusClientsUse_WhenCalled()
    {
        // Act
        CorsPolicy policy = new CorsPolicyBuilder().WithOrigins("https://example.com").WithResourcefullUploadHeaders().Build();

        // Assert
        Assert.Equal(["POST", "HEAD", "PATCH", "DELETE", "OPTIONS"], policy.Methods);
        Assert.Contains("Upload-Metadata", policy.Headers);
        Assert.Contains("X-HTTP-Method-Override", policy.Headers);
        Assert.Contains("Location", policy.ExposedHeaders);
        Assert.Contains("Upload-Offset", policy.ExposedHeaders);
    }
}
