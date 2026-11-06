using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AspNetCore;
using Resourcefull.Testing;

namespace Resourcefull.Tests.AspNetCore;

public class UploadLinkTests
{
    private static HttpContent Text(string text, string contentType = "text/plain") =>
        new StringContent(text, MediaTypeHeaderValue.Parse(contentType));

    private static async Task<string?> ReadAsync(TestApp app, string name) =>
        (await app.Files.DownloadContentAsync(name))?.Content.ToString();

    [Fact]
    public async Task SharedUpload_ShouldStoreTheBody_WhenTheLinkIsForOneName()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { ResourceName = "claims/1042.txt" });

        // Act
        HttpResponseMessage response = await app.Client.PutAsync($"/upload/{token}", Text("hello"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("claims/1042.txt"))!;
        Assert.Equal($"\"{stored.ContentHash}\"", response.Headers.ETag!.Tag);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(("text/plain", "hello"), (stored.ContentType, await ReadAsync(app, "claims/1042.txt")));
    }

    [Fact]
    public async Task SharedUpload_ShouldStoreUnderThePrefix_WhenTheLinkHasAPrefix()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { NamePrefix = "claims/1042/" });

        // Act
        HttpResponseMessage first = await app.Client.PutAsync($"/upload/{token}/photo.jpg", Text("one"));
        HttpResponseMessage second = await app.Client.PutAsync($"/upload/{token}/scans/form.pdf", Text("two"));

        // Assert
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, second.StatusCode));
        Assert.Equal("one", await ReadAsync(app, "claims/1042/photo.jpg"));
        Assert.Equal("two", await ReadAsync(app, "claims/1042/scans/form.pdf"));
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerNotFoundAndStoreNothing_WhenTheNameIsntOneTheLinkCovers()
    {
        // Arrange: a link to one name, given a name of its own, and a link with a prefix, given none.
        await using TestApp app = await TestApp.StartAsync();
        string oneName = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { ResourceName = "a.txt" });
        string prefixed = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { NamePrefix = "in/" });

        // Act
        HttpResponseMessage named = await app.Client.PutAsync($"/upload/{oneName}/b.txt", Text("x"));
        HttpResponseMessage unnamed = await app.Client.PutAsync($"/upload/{prefixed}", Text("x"));

        // Assert
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (named.StatusCode, unnamed.StatusCode));
        Assert.Empty(await app.Files.ListResourcesAsync().ToListAsync());
    }

    [Fact]
    public async Task SharedUpload_ShouldRefuseBeforeReadingTheBody_WhenTheRequestBreaksTheLinksLimits()
    {
        // Arrange: at most 4 bytes of plain text or PDF.
        await using TestApp app = await TestApp.StartAsync();
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container)
        {
            ResourceName = "a.txt", MaxSize = 4, AllowedContentTypes = ["text/plain", "application/pdf"]
        });
        // A stream that can't be measured, so the request has no Content-Length.
        var unmeasured = new StreamContent(new NonSeekableStream([1, 2]));
        unmeasured.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");

        // Act
        HttpResponseMessage tooLarge = await app.Client.PutAsync($"/upload/{token}", Text("12345"));
        HttpResponseMessage wrongType = await app.Client.PutAsync($"/upload/{token}", Text("1234", "text/html"));
        HttpResponseMessage noLength = await app.Client.PutAsync($"/upload/{token}", unmeasured);
        HttpResponseMessage allowed = await app.Client.PutAsync($"/upload/{token}", Text("1234", "application/pdf"));

        // Assert
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.LengthRequired, noLength.StatusCode);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        Assert.Equal("1234", await ReadAsync(app, "a.txt"));
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerConflictAndKeepTheFile_WhenACreateOnlyLinksNameExists()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("first"));
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { ResourceName = "a.txt" });

        // Act
        HttpResponseMessage response = await app.Client.PutAsync($"/upload/{token}", Text("second"));

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already exists", await response.Content.ReadAsStringAsync());
        Assert.Equal("first", await ReadAsync(app, "a.txt"));
    }

    [Fact]
    public async Task SharedUpload_ShouldCreateThenReplace_WhenTheLinkReplaces()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container)
        {
            ResourceName = "a.txt", Mode = ResourceWriteMode.Replace
        });

        // Act
        HttpResponseMessage created = await app.Client.PutAsync($"/upload/{token}", Text("first"));
        HttpResponseMessage replaced = await app.Client.PutAsync($"/upload/{token}", Text("second"));

        // Assert
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (created.StatusCode, replaced.StatusCode));
        Assert.Equal("second", await ReadAsync(app, "a.txt"));
    }

    [Fact]
    public async Task SharedUpload_ShouldStillApplyTheContainersPolicy_WhenTheLinkAllowsTheUpload()
    {
        // Arrange: the link takes any type, the container only PDFs.
        await using TestApp app = await TestApp.StartAsync(policy: new ContainerPolicy { AllowedContentTypes = ["application/pdf"] });
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { ResourceName = "a.txt" });

        // Act
        HttpResponseMessage response = await app.Client.PutAsync($"/upload/{token}", Text("x"));

        // Assert: refused as the exception handler would, without naming the container.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.DoesNotContain(TestApp.Container, await response.Content.ReadAsStringAsync());
        Assert.False(await app.Files.ExistsAsync("a.txt"));
    }

    [Theory]
    [InlineData(ResourceWriteMode.Create)]
    [InlineData(ResourceWriteMode.Replace)]
    public async Task SharedUpload_ShouldAnswerBadRequestAndStoreNothing_WhenGivenANameTheRulesRefuse(ResourceWriteMode mode)
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { NamePrefix = "in/", Mode = mode });

        // Act: a name that ends in a space.
        HttpResponseMessage response = await app.Client.PutAsync($"/upload/{token}/photo.jpg%20", Text("x"));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("in/", await response.Content.ReadAsStringAsync());
        Assert.Empty(await app.Files.ListResourcesAsync().ToListAsync());
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerNotFoundWithoutNamingTheContainer_WhenTheContainerDoesntExist()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = app.Links.CreateUploadToken(new ResourceUploadLink("deleted-claims") { ResourceName = "a.txt" });

        // Act
        HttpResponseMessage response = await app.Client.PutAsync($"/upload/{token}", Text("x"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("deleted-claims", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SharedUpload_ShouldAnswerGone_WhenTheLinkHasExpired()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using TestApp app = await TestApp.StartAsync(clock: clock);
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container)
        {
            ResourceName = "a.txt", ExpiresIn = TimeSpan.FromMinutes(10)
        });

        // Act
        clock.Now = clock.Now.AddMinutes(10);
        HttpResponseMessage response = await app.Client.PutAsync($"/upload/{token}", Text("x"));

        // Assert
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.False(await app.Files.ExistsAsync("a.txt"));
    }

    [Fact]
    public async Task SharedUploadAndSharedResource_ShouldAnswerNotFound_WhenGivenTheOtherKindOfToken()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("first"));
        string download = (await app.Links.CreateTokenAsync(new ResourceLink(TestApp.Container, "a.txt")))!;
        string upload = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container)
        {
            ResourceName = "a.txt", Mode = ResourceWriteMode.Replace
        });

        // Act
        HttpResponseMessage uploadedWithDownloadToken = await app.Client.PutAsync($"/upload/{download}", Text("second"));
        HttpResponseMessage downloadedWithUploadToken = await app.Client.GetAsync($"/shared/{upload}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, uploadedWithDownloadToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, downloadedWithUploadToken.StatusCode);
        Assert.Equal("first", await ReadAsync(app, "a.txt"));
    }

    [Fact]
    public async Task CreateUploadToken_ShouldThrow_WhenTheLinkIsMalformed()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        var link = new ResourceUploadLink(TestApp.Container) { ResourceName = "a.txt" };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => app.Links.CreateUploadToken(link with { NamePrefix = "in/" }));
        Assert.Throws<ArgumentException>(() => app.Links.CreateUploadToken(link with { ResourceName = null }));
        Assert.Throws<ArgumentException>(() => app.Links.CreateUploadToken(link with { ResourceName = " a.txt" }));
        Assert.Throws<ArgumentException>(() => app.Links.CreateUploadToken(link with { ResourceName = null, NamePrefix = "\tin/" }));
        Assert.Throws<ArgumentException>(() => app.Links.CreateUploadToken(new ResourceUploadLink(" ") { ResourceName = "a.txt" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => app.Links.CreateUploadToken(link with { ExpiresIn = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => app.Links.CreateUploadToken(link with { MaxSize = -1 }));
    }

    [Fact]
    public async Task CheckUpload_ShouldSayWhatTheLinkAllows_WhenTheTokenIsValidOrExpired()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using TestApp app = await TestApp.StartAsync(clock: clock);
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(TestApp.Container)
        {
            NamePrefix = "in/", ExpiresIn = TimeSpan.FromHours(2), MaxSize = 10, AllowedContentTypes = ["image/*"],
            Mode = ResourceWriteMode.Replace
        });

        // Act
        ResourceUploadLinkCheck valid = app.Links.CheckUpload(token);
        clock.Now = clock.Now.AddHours(3);
        ResourceUploadLinkCheck expired = app.Links.CheckUpload(token);

        // Assert
        Assert.Equal(ResourceLinkStatus.Valid, valid.Status);
        Assert.Equal(ResourceLinkStatus.Expired, expired.Status);
        SignedResourceUploadLink allowed = valid.Link!;
        Assert.Equal((TestApp.Container, (string?)null, "in/", (long?)10, ResourceWriteMode.Replace, new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero)),
            (allowed.ContainerName, allowed.ResourceName, allowed.NamePrefix, allowed.MaxSize, allowed.Mode, allowed.ExpiresAt));
        Assert.Equal(["image/*"], allowed.AllowedContentTypes!);
        Assert.Equal(new ResourceUploadLinkCheck(ResourceLinkStatus.Invalid, null), app.Links.CheckUpload(""));
    }

    [Fact]
    public async Task SharedUpload_ShouldSayWhatToAdd_WhenSignedLinksArentRegistered()
    {
        // Arrange: a request whose services have no link signer.
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        // Act
        Task act() => Results.SharedUpload(new Moq.Mock<IResourceServiceClient>().Object, "token").ExecuteAsync(context);

        // Assert
        Assert.Contains("AddResourcefullLinks", (await Assert.ThrowsAsync<InvalidOperationException>(act)).Message);
    }

    [Fact]
    public async Task SharedUpload_ShouldStoreInTheNamedStore_WhenTheLinkIsFromThatStore()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        string token = app.ArchiveLinks.CreateUploadToken(new ResourceUploadLink(TestApp.Container) { ResourceName = "claims/1042.txt" });

        // Act
        HttpResponseMessage response = await app.Client.PutAsync($"/archive/upload/{token}", Text("hello"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("hello", (await app.ArchiveFiles.DownloadContentAsync("claims/1042.txt"))?.Content.ToString());
        Assert.Null(await app.Files.GetResourceInfoAsync("claims/1042.txt"));
    }

    [Theory]
    [InlineData(false, "/archive/upload/")]
    [InlineData(true, "/upload/")]
    public async Task SharedUpload_ShouldAnswerNotFoundAndStoreNothing_WhenTheLinkIsFromAnotherStore(bool fromArchive, string endpoint)
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        var link = new ResourceUploadLink(TestApp.Container) { ResourceName = "claims/1042.txt" };
        string token = (fromArchive ? app.ArchiveLinks : app.Links).CreateUploadToken(link);

        // Act
        HttpResponseMessage response = await app.Client.PutAsync(endpoint + token, Text("hello"));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(await app.Files.GetResourceInfoAsync("claims/1042.txt"));
        Assert.Null(await app.ArchiveFiles.GetResourceInfoAsync("claims/1042.txt"));
    }

    /// <summary>A stream whose length can't be known in advance.</summary>
    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
