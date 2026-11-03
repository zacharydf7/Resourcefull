using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.AspNetCore;

/// <summary>The dashboard's changes, through the test app's dashboard at <c>/editable</c>, which allows them.</summary>
public class DashboardChangeTests
{
    /// <summary>A change as the page sends it, with its header.</summary>
    private static HttpRequestMessage Change(HttpMethod method, string path, object? json = null)
    {
        var request = new HttpRequestMessage(method, "/editable/api/" + path);
        request.Headers.Add("X-Resourcefull-Dashboard", "1");
        if (json is not null) request.Content = JsonContent.Create(json);
        return request;
    }

    private static HttpRequestMessage Upload(string name, string content, bool replace = false)
    {
        HttpRequestMessage request = Change(HttpMethod.Put,
            $"upload?container={TestApp.Container}&name={Uri.EscapeDataString(name)}" + (replace ? "&replace=true" : ""));
        request.Content = new StringContent(content, Encoding.UTF8, "text/plain");
        return request;
    }

    private static async Task<string?> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString();

    [Fact]
    public async Task Changes_ShouldNotBeMapped_WhenAllowChangesIsOff()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/resourcefull/api/container?name={TestApp.Container}");
        request.Headers.Add("X-Resourcefull-Dashboard", "1");

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(request);
        JsonElement features = await app.Client.GetFromJsonAsync<JsonElement>("/resourcefull/api/features");

        // Assert
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.False(features.GetProperty("changes").GetBoolean());
        Assert.True(await app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>()
            .ContainerExistsAsync(TestApp.Container));
    }

    [Fact]
    public async Task Features_ShouldSayWhichChangesTheDashboardAllows_WhenCalled()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        JsonElement features = await app.Client.GetFromJsonAsync<JsonElement>("/editable/api/features");

        // Assert
        Assert.Equal((true, true, true), (features.GetProperty("changes").GetBoolean(),
            features.GetProperty("downloadLinks").GetBoolean(), features.GetProperty("uploadLinks").GetBoolean()));
    }

    private static HttpRequestMessage DeleteFrom(string site)
    {
        HttpRequestMessage request = Change(HttpMethod.Delete, $"resource?container={TestApp.Container}&name=a.txt");
        request.Headers.Add("Sec-Fetch-Site", site);
        return request;
    }

    [Fact]
    public async Task Changes_ShouldBeRefusedAndLogged_WhenTheyLackThePagesHeaderOrComeFromAnotherOrigin()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("a"));

        // Act
        HttpResponseMessage withoutHeader = await app.Client.DeleteAsync($"/editable/api/resource?container={TestApp.Container}&name=a.txt");
        HttpResponseMessage fromAnotherSite = await app.Client.SendAsync(DeleteFrom("cross-site"));
        HttpResponseMessage fromAnotherOriginOfTheSite = await app.Client.SendAsync(DeleteFrom("same-site"));

        // Assert
        Assert.All(new[] { withoutHeader, fromAnotherSite, fromAnotherOriginOfTheSite }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.True(await app.Files.ExistsAsync("a.txt"));
        Assert.Equal(3, app.Logs.Messages.Count(m => m.Category == "Resourcefull.AspNetCore.Dashboard"
            && m.Message.StartsWith("Dashboard refused a change, DELETE /editable/api/resource", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Changes_ShouldBeMade_WhenTheBrowserSaysTheyCameFromTheDashboardsOrigin()
    {
        // Arrange: what a browser sends with a fetch from the page.
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("a"));

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(DeleteFrom("same-origin"));

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(await app.Files.ExistsAsync("a.txt"));
    }

    [Fact]
    public async Task Changes_ShouldAnswerBadRequest_WhenGivenNamesOrIdsTheyCantUse()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("a"));

        // Act
        HttpResponseMessage spacedName = await app.Client.SendAsync(Change(HttpMethod.Post, $"upload-link?container={TestApp.Container}",
            new { name = "a.txt " }));
        HttpResponseMessage noContainerName = await app.Client.SendAsync(Change(HttpMethod.Post, "containers", new { }));
        HttpResponseMessage noVersionId = await app.Client.SendAsync(Change(HttpMethod.Post,
            $"restore?container={TestApp.Container}&name=a.txt&versionId="));

        // Assert
        Assert.All(new[] { spacedName, noContainerName, noVersionId }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Contains("name rules", await TitleOf(spacedName), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_ShouldStoreTheBodyAndLogWhoUploadedIt_WhenSentFromThePage()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        HttpResponseMessage created = await app.Client.SendAsync(Upload("a.txt", "first"));
        HttpResponseMessage taken = await app.Client.SendAsync(Upload("a.txt", "second"));
        HttpResponseMessage replaced = await app.Client.SendAsync(Upload("a.txt", "third", replace: true));

        // Assert
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.Created),
            (created.StatusCode, taken.StatusCode, replaced.StatusCode));
        Assert.Equal("third", (await app.Files.DownloadContentAsync("a.txt"))!.Content.ToString());
        Assert.Equal("text/plain; charset=utf-8", (await app.Files.GetResourceInfoAsync("a.txt"))!.ContentType);
        Assert.Equal(2, app.Logs.Messages.Count(m => m.Category == "Resourcefull.AspNetCore.Dashboard"
            && m.Message.Contains("upload by an anonymous user in container files, resource a.txt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task VersionChanges_ShouldRestoreChangeAndDeleteVersions_WhenSentFromThePage()
    {
        // Arrange: two versions of a name.
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo older = await app.Files.UploadAsync("a.txt", BinaryData.FromString("older"));
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("newer"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        string file = $"container={TestApp.Container}&name=a.txt";
        var expiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        HttpResponseMessage restored = await app.Client.SendAsync(Change(HttpMethod.Post, $"restore?{file}&versionId={older.VersionId}"));
        HttpResponseMessage expiry = await app.Client.SendAsync(Change(HttpMethod.Put, $"expiry?{file}", new { expiresAt }));
        HttpResponseMessage contentType = await app.Client.SendAsync(Change(HttpMethod.Put, $"content-type?{file}", new { contentType = "text/csv" }));
        HttpResponseMessage metadata = await app.Client.SendAsync(Change(HttpMethod.Put, $"metadata?{file}",
            new { metadata = new Dictionary<string, string> { ["owner"] = "finance" } }));
        HttpResponseMessage deleted = await app.Client.SendAsync(Change(HttpMethod.Delete, $"resource?{file}&versionId={older.VersionId}"));
        HttpResponseMessage missing = await app.Client.SendAsync(Change(HttpMethod.Delete, $"resource?{file}&versionId={older.VersionId}"));

        // Assert
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.All(new[] { expiry, contentType, metadata, deleted }, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        ResourceInfo current = (await app.Files.GetResourceInfoAsync("a.txt"))!;
        Assert.Equal("older", (await app.Files.DownloadContentAsync("a.txt"))!.Content.ToString());
        Assert.Equal((expiresAt, "text/csv", "finance"), (current.ExpiresAt, current.ContentType, current.Metadata["owner"]));
        Assert.Equal(2, (await app.Files.GetResourceVersionsAsync("a.txt")).Count);
    }

    [Fact]
    public async Task Containers_ShouldBeCreatedAndDeleted_WhenSentFromThePage()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        IResourceServiceClient resources = app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>();

        // Act
        HttpResponseMessage created = await app.Client.SendAsync(Change(HttpMethod.Post, "containers", new { name = "new" }));
        HttpResponseMessage again = await app.Client.SendAsync(Change(HttpMethod.Post, "containers", new { name = "new" }));
        bool existed = await resources.ContainerExistsAsync("new");
        HttpResponseMessage deleted = await app.Client.SendAsync(Change(HttpMethod.Delete, "container?name=new"));
        HttpResponseMessage missing = await app.Client.SendAsync(Change(HttpMethod.Delete, "container?name=new"));

        // Assert
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.NoContent, HttpStatusCode.NotFound),
            (created.StatusCode, again.StatusCode, deleted.StatusCode, missing.StatusCode));
        Assert.Contains("new", await TitleOf(again), StringComparison.Ordinal);
        Assert.True(existed);
        Assert.False(await resources.ContainerExistsAsync("new"));
    }

    [Fact]
    public async Task Changes_ShouldSayWhyTheStoreRefusedThem_WhenAPolicyRefuses()
    {
        // Arrange: containers that are read-only.
        await using TestApp app = await TestApp.StartAsync(policy: new ResourceContainerPolicy { ReadOnly = true });

        // Act
        HttpResponseMessage response = await app.Client.SendAsync(Upload("a.txt", "a"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("read-only", await TitleOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadLink_ShouldGiveAUrlThatDownloadsTheVersion_WhenSentFromThePage()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        ResourceInfo stored = await app.Files.UploadAsync("a.txt", BinaryData.FromString("hello"));
        string file = $"container={TestApp.Container}&name=a.txt";

        // Act
        HttpResponseMessage created = await app.Client.SendAsync(Change(HttpMethod.Post, $"download-link?{file}&versionId={stored.VersionId}",
            new { expiresInMinutes = 30 }));
        JsonElement link = await created.Content.ReadFromJsonAsync<JsonElement>();
        string downloaded = await app.Client.GetStringAsync(link.GetProperty("url").GetString());
        HttpResponseMessage tooLong = await app.Client.SendAsync(Change(HttpMethod.Post, $"download-link?{file}", new { expiresInMinutes = 20_000 }));
        HttpResponseMessage missing = await app.Client.SendAsync(Change(HttpMethod.Post, $"download-link?container={TestApp.Container}&name=b.txt",
            new { expiresInMinutes = 30 }));

        // Assert
        Assert.StartsWith("http://localhost/shared/", link.GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.Equal("hello", downloaded);
        Assert.InRange(link.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (tooLong.StatusCode, missing.StatusCode));
    }

    [Fact]
    public async Task UploadLink_ShouldGiveAUrlThatUploadsUnderItsPrefix_WhenSentFromThePage()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        HttpResponseMessage created = await app.Client.SendAsync(Change(HttpMethod.Post, $"upload-link?container={TestApp.Container}",
            new { namePrefix = "in/", expiresInMinutes = 60 }));
        string url = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        HttpResponseMessage uploaded = await app.Client.PutAsync(url + "/b.txt", new StringContent("b", Encoding.UTF8, "text/plain"));
        HttpResponseMessage neither = await app.Client.SendAsync(Change(HttpMethod.Post, $"upload-link?container={TestApp.Container}",
            new { expiresInMinutes = 60 }));
        HttpResponseMessage missing = await app.Client.SendAsync(Change(HttpMethod.Post, "upload-link?container=missing",
            new { name = "a.txt" }));

        // Assert
        Assert.True(uploaded.IsSuccessStatusCode);
        Assert.Equal("b", (await app.Files.DownloadContentAsync("in/b.txt"))!.Content.ToString());
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (neither.StatusCode, missing.StatusCode));
    }

    [Theory]
    [InlineData("/shared", null)]
    [InlineData(null, "/upload")]
    [InlineData("shared/{token}", null)]
    public void MapResourcefullDashboard_ShouldThrowArgumentException_WhenGivenALinkPathWithoutAToken(string? downloadPath, string? uploadPath)
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddResourcefull(options => options.UseInMemory());
        WebApplication app = builder.Build();

        // Act
        void act() => app.MapResourcefullDashboard("/dev", new ResourcefullDashboardOptions
        {
            AllowChanges = true, DownloadLinkPath = downloadPath, UploadLinkPath = uploadPath
        });

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void MapResourcefullDashboard_ShouldThrowInvalidOperationException_WhenGivenALinkPathWithoutTheLinkSigner()
    {
        // Arrange: no AddResourcefullLinks.
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddResourcefull(options => options.UseInMemory());
        WebApplication app = builder.Build();

        // Act
        void act() => app.MapResourcefullDashboard("/dev", new ResourcefullDashboardOptions { AllowChanges = true, UploadLinkPath = "/upload/{token}" });

        // Assert
        Assert.Contains("AddResourcefullLinks", Assert.Throws<InvalidOperationException>(act).Message, StringComparison.Ordinal);
    }
}
