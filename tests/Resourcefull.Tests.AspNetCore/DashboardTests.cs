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
using Moq;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.AspNetCore;

public class DashboardTests
{
    /// <summary>
    /// Starts an app with the dashboard, where a request with an <c>X-User</c> header is signed in as that name, and one
    /// with <c>X-User-Id</c> with only that ID, and returns a client for it.
    /// </summary>
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<RouteGroupBuilder> configureDashboard,
        Action<AuthorizationOptions>? configureAuthorization = null, Action<IServiceCollection>? configureServices = null,
        ResourcefullDashboardOptions? dashboardOptions = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddResourcefull(options => options.UseInMemory());
        configureServices?.Invoke(builder.Services);
        builder.Services.AddAuthentication(HeaderAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>(HeaderAuthentication.SchemeName, null);
        builder.Services.AddAuthorization(configureAuthorization ?? (_ => { }));
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        configureDashboard(app.MapResourcefullDashboard(options: dashboardOptions));
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
        await using TestApp app = await TestApp.StartAsync(policy: new ContainerPolicy
        {
            MaxContainerSize = ResourceSize.Parse("1 MB"), MaxResourceCount = 10, AllowedContentTypes = ["text/plain"]
        });
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("hello"), new ResourceUploadOptions { Headers =
            new ResourceHeaders { ContentType = "text/plain" } });
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
        Assert.Equal((5L, 1L, 1L, 0L, 0L), (usage.GetProperty("storedSize").GetInt64(), usage.GetProperty("resourceCount").GetInt64(),
            usage.GetProperty("versionCount").GetInt64(), usage.GetProperty("deletedSize").GetInt64(),
            usage.GetProperty("deletedVersionCount").GetInt64()));
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
            new ResourceUploadOptions { Headers =
                new ResourceHeaders { ContentType = "text/plain" }, ExpiresAt = expiresAt });
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

    /// <summary>Each item's name, size and whether it's the current version.</summary>
    private static List<(string?, long, bool)> Versions(JsonElement list) => [.. list.GetProperty("items").EnumerateArray()
        .Select(v => (v.GetProperty("name").GetString(), v.GetProperty("contentLength").GetInt64(), v.GetProperty("isLatest").GetBoolean()))];

    [Fact]
    public async Task Largest_ShouldListTheContainersLargestVersionsOrAnswerNotFound_WhenGivenAContainer()
    {
        // Arrange: an older version among the current ones.
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("abc"));
        await app.Files.UploadAsync("a.txt", BinaryData.FromString("abcdefghij"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await app.Files.UploadAsync("b.txt", BinaryData.FromString("abcde"));

        // Act
        JsonElement largest = await app.Client.GetFromJsonAsync<JsonElement>($"/resourcefull/api/largest?container={TestApp.Container}");
        HttpResponseMessage missing = await app.Client.GetAsync("/resourcefull/api/largest?container=missing");

        // Assert
        Assert.Equal([("a.txt", 10L, true), ("b.txt", 5L, true), ("a.txt", 3L, false)], Versions(largest));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Expiring_ShouldListTheVersionsThatExpireSoonestFirst_WhenGivenAContainer()
    {
        // Arrange
        await using TestApp app = await TestApp.StartAsync();
        var expiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await app.Files.UploadAsync("later.txt", BinaryData.FromString("a"), new ResourceUploadOptions { ExpiresAt = expiresAt.AddDays(1) });
        await app.Files.UploadAsync("sooner.txt", BinaryData.FromString("b"), new ResourceUploadOptions { ExpiresAt = expiresAt });
        await app.Files.UploadAsync("kept.txt", BinaryData.FromString("c"));

        // Act
        JsonElement expiring = await app.Client.GetFromJsonAsync<JsonElement>($"/resourcefull/api/expiring?container={TestApp.Container}");

        // Assert
        Assert.Equal(["sooner.txt", "later.txt"], expiring.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("name").GetString()));
        Assert.Equal(expiresAt, expiring.GetProperty("items")[0].GetProperty("expiresAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Versions_ShouldListAFilesVersionsOrAnswerNotFoundOrBadRequest_WhenGivenAName()
    {
        // Arrange: a name with characters the query string has to escape.
        await using TestApp app = await TestApp.StartAsync();
        await app.Files.UploadAsync("a&b=c.txt", BinaryData.FromString("abc"));
        await app.Files.UploadAsync("a&b=c.txt", BinaryData.FromString("abcdefghij"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        JsonElement versions = await app.Client.GetFromJsonAsync<JsonElement>(
            $"/resourcefull/api/versions?container={TestApp.Container}&name={Uri.EscapeDataString("a&b=c.txt")}");
        HttpResponseMessage missing = await app.Client.GetAsync($"/resourcefull/api/versions?container={TestApp.Container}&name=missing.txt");
        HttpResponseMessage invalid = await app.Client.GetAsync($"/resourcefull/api/versions?container={TestApp.Container}&name=%20a");

        // Assert
        Assert.Equal([("a&b=c.txt", 10L, true), ("a&b=c.txt", 3L, false)], Versions(versions));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Expired_ShouldCountTheExpiredVersionsWaitingForTheSweep_WhenSomeHaveExpired()
    {
        // Arrange: two expired versions in one store, and 101 in another, more than the dashboard counts.
        await using TestApp few = await TestApp.StartAsync();
        await using TestApp many = await TestApp.StartAsync();
        var oldest = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await few.Files.UploadAsync("a.txt", BinaryData.FromString("a"), new ResourceUploadOptions { ExpiresAt = oldest.AddDays(1) });
        await few.Files.UploadAsync("b.txt", BinaryData.FromString("b"), new ResourceUploadOptions { ExpiresAt = oldest });
        await few.Files.UploadAsync("c.txt", BinaryData.FromString("c"));
        for (int i = 0; i < 101; i++)
            await many.Files.UploadAsync($"{i}.txt", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = oldest });

        // Act
        JsonElement someExpired = await few.Client.GetFromJsonAsync<JsonElement>("/resourcefull/api/expired");
        JsonElement manyExpired = await many.Client.GetFromJsonAsync<JsonElement>("/resourcefull/api/expired");

        // Assert
        Assert.Equal((true, 2, false), (someExpired.GetProperty("supported").GetBoolean(), someExpired.GetProperty("count").GetInt32(),
            someExpired.GetProperty("more").GetBoolean()));
        Assert.Equal(oldest, someExpired.GetProperty("oldestExpiry").GetDateTimeOffset());
        Assert.Equal((100, true), (manyExpired.GetProperty("count").GetInt32(), manyExpired.GetProperty("more").GetBoolean()));
    }

    [Fact]
    public async Task Expired_ShouldSayExpiryIsNotSupportedAndAskOnlyOnce_WhenTheProviderDoesNotSupportIt()
    {
        // Arrange: a service client over a provider that doesn't support expiry.
        var resources = new Mock<IResourceServiceClient>();
        resources.Setup(r => r.GetExpiredVersionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("No expiry."));
        (WebApplication app, HttpClient client) = await StartAsync(dashboard => dashboard.AllowAnonymous(),
            configureServices: services => services.AddScoped(_ => resources.Object));
        await using WebApplication _ = app;

        // Act
        JsonElement first = await client.GetFromJsonAsync<JsonElement>("/resourcefull/api/expired");
        JsonElement second = await client.GetFromJsonAsync<JsonElement>("/resourcefull/api/expired");

        // Assert
        Assert.False(first.GetProperty("supported").GetBoolean());
        Assert.False(second.GetProperty("supported").GetBoolean());
        resources.Verify(r => r.GetExpiredVersionsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Changes_ShouldLogTheUsersNameOrId_WhenASignedInUserMakesThem()
    {
        // Arrange: one user signed in with a name, and one whose token carries only an ID.
        var logs = new RecordingLoggerProvider();
        (WebApplication app, HttpClient client) = await StartAsync(dashboard => dashboard.RequireAuthorization(),
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs),
            dashboardOptions: new ResourcefullDashboardOptions { AllowChanges = true });
        await using WebApplication _ = app;
        HttpRequestMessage Create(string header, string user, string container)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/resourcefull/api/containers") { Content = JsonContent.Create(new { name = container }) };
            request.Headers.Add("X-Resourcefull-Dashboard", "1");
            request.Headers.Add(header, user);
            return request;
        }

        // Act
        HttpResponseMessage named = await client.SendAsync(Create("X-User", "admin", "a"));
        HttpResponseMessage identified = await client.SendAsync(Create("X-User-Id", "user-42", "b"));

        // Assert
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (named.StatusCode, identified.StatusCode));
        string[] changes = [.. logs.Messages.Where(m => m.Category == "Resourcefull.AspNetCore.Dashboard").Select(m => m.Message)];
        Assert.Contains(changes, m => m.Contains("create_container by admin in container a", StringComparison.Ordinal));
        Assert.Contains(changes, m => m.Contains("create_container by user-42 in container b", StringComparison.Ordinal));
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
            Task.FromResult(Request.Headers.TryGetValue("X-User", out var user) ? SignedIn(new Claim(ClaimTypes.Name, user.ToString()))
                : Request.Headers.TryGetValue("X-User-Id", out var id) ? SignedIn(new Claim(ClaimTypes.NameIdentifier, id.ToString()))
                : AuthenticateResult.NoResult());

        private static AuthenticateResult SignedIn(Claim claim) =>
            AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([claim], SchemeName)), SchemeName));
    }
}
