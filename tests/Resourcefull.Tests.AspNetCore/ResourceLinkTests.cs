using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AspNetCore;
using Resourcefull.Tests.Contract;

namespace Resourcefull.Tests.AspNetCore;

public class ResourceLinkTests
{
    private static async Task<TestApp> StartWithReportAsync(TimeProvider? clock = null)
    {
        TestApp app = await TestApp.StartAsync(clock: clock);
        await app.Files.UploadAsync("2026/report.txt", BinaryData.FromString("first"),
            new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null) });
        return app;
    }

    private static async Task<string> TokenAsync(TestApp app, ResourceLink? link = null) =>
        (await app.Links.CreateTokenAsync(link ?? new ResourceLink(TestApp.Container, "2026/report.txt")))!;

    [Fact]
    public async Task SharedResource_ShouldOfferTheFileAsADownload_WhenAnInlineLinksFileIsReplacedByAPage()
    {
        // Arrange: an inline link to whatever is current, and the image it was made for replaced by a web page.
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("avatar", BinaryData.FromString("png"), new ResourceUploadOptions { Headers = new ResourceHeaders("image/png", null) });
        string token = await TokenAsync(app, new ResourceLink(TestApp.Container, "avatar") { Inline = true, FollowCurrent = true });
        await app.Files.UploadAsync("avatar", BinaryData.FromString("<script></script>"), new ResourceUploadOptions { Headers = new ResourceHeaders("text/html", null) });

        // Act
        HttpResponseMessage response = await app.Client.GetAsync("/shared/" + token);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    private static void AssertNotPassedOn(HttpResponseMessage response)
    {
        Assert.Equal(["no-referrer"], response.Headers.GetValues("Referrer-Policy"));
        Assert.True(response.Headers.CacheControl!.Private);
    }

    [Fact]
    public async Task SharedResource_ShouldSendTheFileWithoutPassingTheLinkOn_WhenTheLinkIsValid()
    {
        // Arrange
        await using TestApp app = await StartWithReportAsync();
        string token = await TokenAsync(app);

        // Act
        HttpResponseMessage response = await app.Client.GetAsync($"/shared/{token}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("first", await response.Content.ReadAsStringAsync());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("report.txt", response.Content.Headers.ContentDisposition.FileName);
        Assert.DoesNotContain("report", token, StringComparison.OrdinalIgnoreCase);
        AssertNotPassedOn(response);
    }

    [Fact]
    public async Task SharedResource_ShouldSendTheVersionCurrentWhenTheLinkWasMade_WhenANewerOneIsUploaded()
    {
        // Arrange
        await using TestApp app = await StartWithReportAsync();
        string pinned = await TokenAsync(app);
        string following = await TokenAsync(app, new ResourceLink(TestApp.Container, "2026/report.txt") { FollowCurrent = true });
        await app.Files.UploadAsync("2026/report.txt", BinaryData.FromString("second"));

        // Act
        string viaPinned = await app.Client.GetStringAsync($"/shared/{pinned}");
        string viaFollowing = await app.Client.GetStringAsync($"/shared/{following}");

        // Assert
        Assert.Equal("first", viaPinned);
        Assert.Equal("second", viaFollowing);
    }

    [Fact]
    public async Task SharedResource_ShouldAnswerGoneButStillNotPassTheLinkOn_WhenTheLinkHasExpired()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using TestApp app = await StartWithReportAsync(clock);
        string token = await TokenAsync(app, new ResourceLink(TestApp.Container, "2026/report.txt") { ExpiresIn = TimeSpan.FromMinutes(10) });
        HttpResponseMessage before = await app.Client.GetAsync($"/shared/{token}");

        // Act
        clock.Now = clock.Now.AddMinutes(10);
        HttpResponseMessage after = await app.Client.GetAsync($"/shared/{token}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);
        Assert.Empty(await after.Content.ReadAsByteArrayAsync());
        AssertNotPassedOn(after);
    }

    [Fact]
    public async Task SharedResource_ShouldAnswerNotFound_WhenTheTokenWasChangedMadeUpOrSignedByAnotherApp()
    {
        // Arrange
        await using TestApp app = await StartWithReportAsync();
        await using TestApp other = await StartWithReportAsync();
        string token = await TokenAsync(app);
        // A character from the middle: the last one can hold unused bits, which changing wouldn't change the token.
        int middle = token.Length / 2;
        string changed = token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..];
        string fromOtherApp = await TokenAsync(other);

        // Act
        HttpStatusCode[] statuses =
        [
            (await app.Client.GetAsync($"/shared/{changed}")).StatusCode,
            (await app.Client.GetAsync("/shared/not-a-token")).StatusCode,
            (await app.Client.GetAsync($"/shared/{fromOtherApp}")).StatusCode
        ];

        // Assert
        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.NotFound, status));
        AssertNotPassedOn(await app.Client.GetAsync("/shared/not-a-token"));
    }

    [Fact]
    public async Task SharedResource_ShouldAnswerNotFound_WhenTheLinksVersionIsGone()
    {
        // Arrange
        await using TestApp app = await StartWithReportAsync();
        string token = await TokenAsync(app);

        // Act
        await app.Files.DeleteResourceIfExistsAsync("2026/report.txt");
        HttpResponseMessage response = await app.Client.GetAsync($"/shared/{token}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SharedResource_ShouldShowTheFileInlineAndAnswerRanges_WhenTheLinkSaysInline()
    {
        // Arrange
        await using TestApp app = await StartWithReportAsync();
        string token = await TokenAsync(app, new ResourceLink(TestApp.Container, "2026/report.txt") { Inline = true });
        var request = new HttpRequestMessage(HttpMethod.Get, $"/shared/{token}");
        request.Headers.Range = new RangeHeaderValue(1, 3);

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("irs", await response.Content.ReadAsStringAsync());
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task CreateTokenAsync_ShouldReturnNullOrThrow_WhenTheResourceIsMissingOrTheLinkIsMalformed()
    {
        // Arrange
        await using TestApp app = await StartWithReportAsync();

        // Act
        string? missing = await app.Links.CreateTokenAsync(new ResourceLink(TestApp.Container, "missing.txt"));
        string? missingVersion = await app.Links.CreateTokenAsync(new ResourceLink(TestApp.Container, "2026/report.txt") { VersionId = "no-such-version" });
        Task both() => app.Links.CreateTokenAsync(new ResourceLink(TestApp.Container, "2026/report.txt") { VersionId = "v", FollowCurrent = true });
        Task neverValid() => app.Links.CreateTokenAsync(new ResourceLink(TestApp.Container, "2026/report.txt") { ExpiresIn = TimeSpan.Zero });

        // Assert
        Assert.Null(missing);
        Assert.Null(missingVersion);
        await Assert.ThrowsAsync<ArgumentException>(both);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(neverValid);
    }

    [Fact]
    public async Task Check_ShouldSayWhatTheLinkGrants_WhenTheTokenIsValidOrExpired()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        await using TestApp app = await StartWithReportAsync(clock);
        ResourceInfo current = (await app.Files.GetResourceInfoAsync("2026/report.txt"))!;
        string token = await TokenAsync(app, new ResourceLink(TestApp.Container, "2026/report.txt") { ExpiresIn = TimeSpan.FromHours(2), Inline = true });

        // Act
        ResourceLinkCheck valid = app.Links.Check(token);
        clock.Now = clock.Now.AddHours(3);
        ResourceLinkCheck expired = app.Links.Check(token);

        // Assert
        var granted = new SignedResourceLink(TestApp.Container, "2026/report.txt", current.VersionId, new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), true);
        Assert.Equal(new ResourceLinkCheck(ResourceLinkStatus.Valid, granted), valid);
        Assert.Equal(new ResourceLinkCheck(ResourceLinkStatus.Expired, granted), expired);
        Assert.Equal(new ResourceLinkCheck(ResourceLinkStatus.Invalid, null), app.Links.Check(""));
    }

    [Fact]
    public async Task SharedResource_ShouldSayWhatToAdd_WhenSignedLinksArentRegistered()
    {
        // Arrange: a request whose services have no link signer.
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };

        // Act
        Task act() => Results.SharedResource(new Moq.Mock<IResourceServiceClient>().Object, "token").ExecuteAsync(context);

        // Assert
        Assert.Contains("AddResourcefullLinks", (await Assert.ThrowsAsync<InvalidOperationException>(act)).Message);
    }
}
