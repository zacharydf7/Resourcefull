using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AspNetCore;
using Resourcefull.Testing;
using static Resourcefull.Tests.AspNetCore.TusRequests;

namespace Resourcefull.Tests.AspNetCore;

public class ResourceUploadLinkEndpointTests
{
    private static string Token(UploadApp app, string? name = null, string? prefix = null, long? maxSize = null,
        string[]? types = null, ResourceWriteMode mode = ResourceWriteMode.Create) =>
        app.Links.CreateUploadToken(new ResourceUploadLink(UploadApp.Container)
        {
            ResourceName = name, NamePrefix = prefix, MaxSize = maxSize, AllowedContentTypes = types, Mode = mode
        });

    /// <summary>Starts an upload through the link <paramref name="token"/>, and returns its URL.</summary>
    private static async Task<string> StartAsync(UploadApp app, string token, long length, string? user = null,
        params (string Key, string? Value)[] metadata)
    {
        HttpResponseMessage created = await app.Client.SendAsync(Create(length, Metadata(metadata), $"/upload-links/{token}", user));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return created.Headers.Location!.ToString();
    }

    private static Task<HttpResponseMessage> PatchAsync(UploadApp app, string url, long offset, byte[] bytes, string? user = null) =>
        app.Client.SendAsync(Patch(url, offset, bytes, user));

    private static async Task<string?> ReadAsync(IResourceContainerClient container, string name) =>
        (await container.DownloadContentAsync(name))?.Content.ToString();

    [Fact]
    public async Task Upload_ShouldStoreUnderThePrefixAndTheFilesNameWithItsType_WhenTheLinkHasAPrefix()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string token = Token(app, prefix: "claims/1042/", types: ["image/*"]);
        string url = await StartAsync(app, token, 5, metadata: [("filename", "photo.jpg"), ("filetype", "image/jpeg")]);

        // Act
        HttpResponseMessage response = await PatchAsync(app, url, 0, "hello"u8.ToArray());

        // Assert: the upload's URL is under the link's, and the file is stored with its type, and passed to the app.
        Assert.StartsWith($"/upload-links/{token}/", url);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("hello", await ReadAsync(app.Files, "claims/1042/photo.jpg"));
        ResourceInfo stored = (await app.Files.GetResourceInfoAsync("claims/1042/photo.jpg"))!;
        Assert.Equal("image/jpeg", stored.ContentType);
        Assert.Equal(stored.VersionId, Assert.Single(app.Completed).VersionId);
    }

    [Fact]
    public async Task Upload_ShouldStoreUnderTheLinksName_WhenTheLinkIsToOneName()
    {
        // Arrange: tus clients send the file's name whatever the link.
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, Token(app, name: "contract.pdf"), 3, metadata: ("filename", "scan 1.pdf"));

        // Act
        await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Assert
        Assert.Equal("abc", await ReadAsync(app.Files, "contract.pdf"));
        Assert.Null(await app.Files.GetResourceInfoAsync("scan 1.pdf"));
    }

    [Theory]
    [InlineData("expired", HttpStatusCode.Gone)]
    [InlineData("invalid", HttpStatusCode.NotFound)]
    [InlineData("download", HttpStatusCode.NotFound)]
    [InlineData("no-filename", HttpStatusCode.BadRequest)]
    [InlineData("not-a-type", HttpStatusCode.BadRequest)]
    [InlineData("not-ascii", HttpStatusCode.BadRequest)]
    [InlineData("too-large", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("wrong-type", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("no-type", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("taken", HttpStatusCode.Conflict)]
    public async Task Post_ShouldRefuseTheUploadBeforeAnyOfItIsSent_WhenTheLinkDoesntAllowIt(string problem, HttpStatusCode expected)
    {
        // Arrange: a link that takes images of up to 10 bytes under in/, and an upload that breaks one of its rules.
        var clock = new ManualTimeProvider();
        await using UploadApp app = await UploadApp.StartAsync(clock: clock);
        await app.Files.UploadAsync("in/taken.jpg", BinaryData.FromString("theirs"));
        string token = problem switch
        {
            "invalid" => "not-a-token",
            "download" => (await app.Links.CreateTokenAsync(new ResourceLink(UploadApp.Container, "in/taken.jpg")))!,
            _ => Token(app, prefix: "in/", maxSize: 10, types: ["image/*"])
        };
        if (problem == "expired") clock.Now = clock.Now.AddHours(2);
        (string Key, string? Value)[] metadata = problem switch
        {
            "no-filename" => [("filetype", "image/jpeg")],
            "not-a-type" => [("filename", "a.jpg"), ("filetype", "image/jpeg\r\nX-Injected: 1")],
            "not-ascii" => [("filename", "a.jpg"), ("filetype", "image/jpeg; name=\"é\"")],
            "wrong-type" => [("filename", "a.jpg"), ("filetype", "text/html")],
            "no-type" => [("filename", "a.jpg")],
            "taken" => [("filename", "taken.jpg"), ("filetype", "image/jpeg")],
            _ => [("filename", "a.jpg"), ("filetype", "image/jpeg")]
        };

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Create(problem == "too-large" ? 11 : 10, Metadata(metadata),
            $"/upload-links/{token}"));

        // Assert: no upload was started, and the answer doesn't name the container.
        Assert.Equal(expected, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("container", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("policy", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData("no-container", HttpStatusCode.NotFound)]
    [InlineData("endpoints", HttpStatusCode.RequestEntityTooLarge)]
    public async Task Post_ShouldRefuseTheUploadWithoutNamingTheContainer_WhenTheStoreOrTheEndpointsDont(string limit,
        HttpStatusCode expected)
    {
        // Arrange: a link without limits of its own, and a container policy, a missing container or the endpoints'
        // own size limit that refuses an upload of 11 bytes.
        await using UploadApp app = await UploadApp.StartAsync(policy: limit == "policy" ? new ContainerPolicy { MaxResourceSize = 10 } : null,
            maxSize: limit == "endpoints" ? 10 : null);
        string token = app.Links.CreateUploadToken(new ResourceUploadLink(limit == "no-container" ? "gone" : UploadApp.Container)
        {
            NamePrefix = "in/"
        });

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Create(11, Metadata(("filename", "a.bin")), $"/upload-links/{token}"));

        // Assert
        Assert.Equal(expected, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain("container", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upload_ShouldFinish_WhenTheLinkExpiresWhileItsUnderWay()
    {
        // Arrange: an upload started an hour before its link expires.
        var clock = new ManualTimeProvider();
        await using UploadApp app = await UploadApp.StartAsync(clock: clock);
        string token = Token(app, name: "video.mp4");
        string url = await StartAsync(app, token, 6, metadata: ("filename", "video.mp4"));
        await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Act: resumed after the link expired.
        clock.Now = clock.Now.AddHours(2);
        HttpResponseMessage response = await PatchAsync(app, url, 3, "def"u8.ToArray());
        HttpResponseMessage another = await app.Client.SendAsync(Create(6, Metadata(("filename", "video.mp4")), $"/upload-links/{token}"));

        // Assert: the upload finished, but the link can't start another.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("abcdef", await ReadAsync(app.Files, "video.mp4"));
        Assert.Equal(HttpStatusCode.Gone, another.StatusCode);
    }

    [Fact]
    public async Task Upload_ShouldWorkForWhoeverHasItsUrl_WhenItWasStartedSignedIn()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string url = await StartAsync(app, Token(app, name: "a.bin"), 3, user: "alice", metadata: ("filename", "a.bin"));

        // Act
        HttpResponseMessage response = await PatchAsync(app, url, 0, "abc"u8.ToArray());

        // Assert: the link is the permission, so the user who started it doesn't matter.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("abc", await ReadAsync(app.Files, "a.bin"));
    }

    [Fact]
    public async Task Upload_ShouldBeRefusedWithoutNamingTheContainer_WhenAnotherUploadThroughTheLinkTookItsNameFirst()
    {
        // Arrange: two uploads started through one create-only link to one name, as a plain upload could be sent twice.
        await using UploadApp app = await UploadApp.StartAsync();
        string token = Token(app, name: "form.pdf");
        string first = await StartAsync(app, token, 3, metadata: ("filename", "form.pdf"));
        string second = await StartAsync(app, token, 3, metadata: ("filename", "form.pdf"));
        await PatchAsync(app, first, 0, "one"u8.ToArray());

        // Act
        HttpResponseMessage response = await PatchAsync(app, second, 0, "two"u8.ToArray());

        // Assert: the first upload keeps the name, and the refusal says why in words of its own.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("A file with this name already exists.", body);
        Assert.DoesNotContain("container", body);
        Assert.Equal("one", await ReadAsync(app.Files, "form.pdf"));
    }

    [Fact]
    public async Task Upload_ShouldReplaceTheFileEachTime_WhenTheLinkReplaces()
    {
        // Arrange
        await using UploadApp app = await UploadApp.StartAsync();
        string token = Token(app, name: "contract.pdf", mode: ResourceWriteMode.Replace);
        await PatchAsync(app, await StartAsync(app, token, 3, metadata: ("filename", "contract.pdf")), 0, "one"u8.ToArray());

        // Act
        HttpResponseMessage response = await PatchAsync(app, await StartAsync(app, token, 3, metadata: ("filename", "contract.pdf")), 0,
            "two"u8.ToArray());

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("two", await ReadAsync(app.Files, "contract.pdf"));
    }

    [Fact]
    public async Task UploadUrl_ShouldNotWork_WhenUsedWithTheOtherKindOfEndpoints()
    {
        // Arrange: an upload started through a link, and one started at /uploads, whose endpoints are mapped with the
        // same pattern string.
        await using UploadApp app = await UploadApp.StartAsync();
        string token = Token(app, prefix: "in/");
        HttpResponseMessage started = await app.Client.SendAsync(Create(3, Metadata(("filename", "a.bin")), $"/links/uploads/{token}"));
        string throughLink = started.Headers.Location!.ToString();
        HttpResponseMessage created = await app.Client.SendAsync(Create(3, Metadata(("filename", "b.bin"))));
        string signedIn = created.Headers.Location!.ToString();

        // Act: each upload's own token, under the other's endpoints.
        HttpResponseMessage atUploads = await PatchAsync(app, "/uploads/" + throughLink.Split('/')[^1], 0, "abc"u8.ToArray());
        HttpResponseMessage atLinks = await PatchAsync(app, $"/links/uploads/{token}/" + signedIn.Split('/')[^1], 0, "abc"u8.ToArray());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, atUploads.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, atLinks.StatusCode);
        Assert.Null(await app.Files.GetResourceInfoAsync("in/a.bin"));
        Assert.Null(await app.Files.GetResourceInfoAsync("tus/b.bin"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("archive")]
    public void MapResourcefullUploadLinks_ShouldSayWhatToAdd_WhenTheStoresSignedLinksArentAdded(string? storeName)
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddResourcefull(options => options.UseInMemory());
        builder.Services.AddKeyedResourcefull("archive", options => options.UseInMemory());
        builder.Services.AddDataProtection();
        using WebApplication app = builder.Build();

        // Act
        void act() => app.MapResourcefullUploadLinks("/upload-links", options => options.StoreName = storeName);

        // Assert
        string expected = storeName is null ? "AddResourcefullLinks." : "AddResourcefullLinks(\"archive\")";
        Assert.Contains(expected, Assert.Throws<InvalidOperationException>(act).Message);
    }
}
