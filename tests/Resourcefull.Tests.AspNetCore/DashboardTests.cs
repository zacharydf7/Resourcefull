using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.AspNetCore;

public class DashboardTests
{
    /// <summary>
    /// Starts an app with the dashboard, where a request with an <c>X-User</c> header is signed in, and returns a client
    /// for it.
    /// </summary>
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<RouteGroupBuilder> configureDashboard,
        Action<AuthorizationOptions>? configureAuthorization = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddResourcefull(options => options.UseInMemory());
        builder.Services.AddAuthentication(HeaderAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.SchemeName, null);
        builder.Services.AddAuthorization(configureAuthorization ?? (_ => { }));
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        configureDashboard(app.MapResourcefullDashboard());
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage SignedIn(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-User", "admin");
        return request;
    }

    [Fact]
    public async Task MapResourcefullDashboard_ShouldThrowSayingWhatToAdd_WhenNothingDecidesWhoMaySeeIt()
    {
        // Arrange
        (WebApplication app, HttpClient client) = await StartAsync(_ => { });
        await using WebApplication _ = app;

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("/resourcefull/api/containers"));

        // Assert
        Assert.Contains("RequireAuthorization", exception.Message);
        Assert.Contains("AllowAnonymous", exception.Message);
    }

    [Fact]
    public async Task MapResourcefullDashboard_ShouldAnswerOnlySignedInUsers_WhenItRequiresAuthorization()
    {
        // Arrange
        (WebApplication app, HttpClient client) = await StartAsync(dashboard => dashboard.RequireAuthorization());
        await using WebApplication _ = app;

        // Act
        HttpResponseMessage anonymousPage = await client.GetAsync("/resourcefull/");
        HttpResponseMessage anonymousApi = await client.GetAsync("/resourcefull/api/containers");
        HttpResponseMessage signedIn = await client.SendAsync(SignedIn("/resourcefull/api/containers"));

        // Assert
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (anonymousPage.StatusCode, anonymousApi.StatusCode));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    [Fact]
    public async Task MapResourcefullDashboard_ShouldRun_WhenAFallbackPolicyCoversIt()
    {
        // Arrange
        (WebApplication app, HttpClient client) = await StartAsync(_ => { },
            options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        await using WebApplication _ = app;

        // Act
        HttpResponseMessage anonymous = await client.GetAsync("/resourcefull/api/containers");
        HttpResponseMessage signedIn = await client.SendAsync(SignedIn("/resourcefull/api/containers"));

        // Assert
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.OK), (anonymous.StatusCode, signedIn.StatusCode));
    }

    [Fact]
    public async Task Page_ShouldRedirectToItsSlashAndServeItsFiles_WhenOpened()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        HttpResponseMessage withoutSlash = await app.Client.GetAsync("/resourcefull?x=1");
        HttpResponseMessage page = await app.Client.GetAsync("/resourcefull/");
        HttpResponseMessage script = await app.Client.GetAsync("/resourcefull/dashboard.js");
        HttpResponseMessage styles = await app.Client.GetAsync("/resourcefull/dashboard.css");

        // Assert
        Assert.Equal(HttpStatusCode.Redirect, withoutSlash.StatusCode);
        Assert.Equal("/resourcefull/?x=1", withoutSlash.Headers.Location!.OriginalString);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<script src=\"dashboard.js\"", await page.Content.ReadAsStringAsync());
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        Assert.Contains("api(\"containers\"", await script.Content.ReadAsStringAsync());
        Assert.Equal("text/css", styles.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Containers_ShouldListEachContainersUsageAndPolicyAPageAtATime_WhenCalled()
    {
        // Arrange: the test app's container, holding one file, and 50 more.
        await using TestApp app = await TestApp.StartAsync(policy: new ResourceContainerPolicy
        {
            MaxContainerSize = ResourceSize.Parse("1 MB"), MaxResourceCount = 10, AllowedContentTypes = ["text/plain"]
        });
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("hello"), new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null) });
        IResourceServiceClient resources = app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>();
        for (int i = 0; i < 50; i++) await resources.CreateContainerAsync($"other-{i:00}");

        // Act
        JsonElement first = await app.Client.GetFromJsonAsync<JsonElement>("/resourcefull/api/containers");
        string token = first.GetProperty("continuationToken").GetString()!;
        JsonElement second = await app.Client.GetFromJsonAsync<JsonElement>($"/resourcefull/api/containers?continuationToken={Uri.EscapeDataString(token)}");
        JsonElement filtered = await app.Client.GetFromJsonAsync<JsonElement>("/resourcefull/api/containers?prefix=fil");

        // Assert
        Assert.Equal((50, 1), (first.GetProperty("items").GetArrayLength(), second.GetProperty("items").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("continuationToken").ValueKind);
        JsonElement files = Assert.Single(filtered.GetProperty("items").EnumerateArray());
        Assert.Equal(TestApp.Container, files.GetProperty("name").GetString());
        JsonElement usage = files.GetProperty("usage");
        Assert.Equal((5L, 1L, 1L), (usage.GetProperty("storedSize").GetInt64(), usage.GetProperty("resourceCount").GetInt64(),
            usage.GetProperty("versionCount").GetInt64()));
        JsonElement policy = files.GetProperty("policy");
        Assert.Equal((1L << 20, 10L, false), (policy.GetProperty("maxContainerSize").GetInt64(),
            policy.GetProperty("maxResourceCount").GetInt64(), policy.GetProperty("readOnly").GetBoolean()));
        Assert.Equal("text/plain", Assert.Single(policy.GetProperty("allowedContentTypes").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Containers_ShouldAnswerBadRequest_WhenGivenAMalformedTokenOrAnInvalidName()
    {
        // Arrange: an app without Resourcefull's exception handler, so only the dashboard can answer 400.
        (WebApplication app, HttpClient client) = await StartAsync(dashboard => dashboard.AllowAnonymous());
        await using WebApplication _ = app;

        // Act
        HttpResponseMessage token = await client.GetAsync("/resourcefull/api/containers?continuationToken=not-a-token");
        HttpResponseMessage name = await client.GetAsync("/resourcefull/api/resources?container=%20padded");

        // Assert
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (token.StatusCode, name.StatusCode));
    }

    [Fact]
    public async Task Container_ShouldDescribeTheContainerOrAnswerNotFound_WhenGivenAName()
    {
        // Arrange: a container whose name has a slash, which the query string carries.
        await using TestApp app = await TestApp.StartAsync();
        IResourceServiceClient resources = app.Services.CreateScope().ServiceProvider.GetRequiredService<IResourceServiceClient>();
        await resources.CreateContainerAsync("team/reports", new Dictionary<string, string> { ["owner"] = "finance" });

        // Act
        JsonElement found = await app.Client.GetFromJsonAsync<JsonElement>("/resourcefull/api/container?name=team%2Freports");
        HttpResponseMessage missing = await app.Client.GetAsync("/resourcefull/api/container?name=missing");

        // Assert
        Assert.Equal("team/reports", found.GetProperty("name").GetString());
        Assert.Equal("finance", found.GetProperty("metadata").GetProperty("owner").GetString());
        Assert.Equal(0, found.GetProperty("usage").GetProperty("resourceCount").GetInt64());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Resources_ShouldListAContainersFilesOrAnswerNotFound_WhenGivenAContainer()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        var expiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await app.Files.UploadAsync("reports/a.txt", BinaryData.FromString("abc"),
            new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null), ExpiresAt = expiresAt });
        await app.Files.UploadAsync("other.txt", BinaryData.FromString("x"));

        // Act
        JsonElement page = await app.Client.GetFromJsonAsync<JsonElement>($"/resourcefull/api/resources?container={TestApp.Container}&prefix=reports/");
        HttpResponseMessage missing = await app.Client.GetAsync("/resourcefull/api/resources?container=missing");

        // Assert
        JsonElement file = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(("reports/a.txt", 3L, "text/plain"), (file.GetProperty("name").GetString(),
            file.GetProperty("contentLength").GetInt64(), file.GetProperty("contentType").GetString()));
        Assert.Equal(expiresAt, file.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Health_ShouldReportTheResourcefullCheck_WhenItsRegistered()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();

        // Act
        JsonElement report = await app.Client.GetFromJsonAsync<JsonElement>("/resourcefull/api/health");

        // Assert
        Assert.Equal("Healthy", report.GetProperty("status").GetString());
        JsonElement check = Assert.Single(report.GetProperty("checks").EnumerateArray());
        Assert.Equal(("resourcefull", "Healthy"), (check.GetProperty("name").GetString(), check.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task Health_ShouldReportNoStatus_WhenNoHealthChecksAreRegistered()
    {
        // Arrange
        (WebApplication app, HttpClient client) = await StartAsync(dashboard => dashboard.AllowAnonymous());
        await using WebApplication _ = app;

        // Act
        JsonElement report = await client.GetFromJsonAsync<JsonElement>("/resourcefull/api/health");

        // Assert
        Assert.Equal(JsonValueKind.Null, report.GetProperty("status").ValueKind);
        Assert.Equal(0, report.GetProperty("checks").GetArrayLength());
    }

    /// <summary>Signs in a request that has an <c>X-User</c> header, as that user.</summary>
    private sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Header";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.TryGetValue("X-User", out var user)
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], SchemeName)), SchemeName))
                : AuthenticateResult.NoResult());
    }
}
