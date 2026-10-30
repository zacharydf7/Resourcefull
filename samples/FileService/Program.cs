// A file service shaped like a production deployment: each signed-in user has a container of their own, held to a
// storage quota and to the file types the service accepts; files are served with ranges and caching headers; users
// can share a file through a link that expires; and the service reports its health and its telemetry.
using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.StaticFiles;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Resourcefull;
using Resourcefull.AspNetCore;
using Resourcefull.InMemory;
using Resourcefull.SqlServer;

var builder = WebApplication.CreateBuilder(args);

// SQL Server when the Resources connection string is set; otherwise memory, so the sample runs as it is, losing its
// files when it stops.
string? connectionString = builder.Configuration.GetConnectionString("Resources");
bool inMemory = string.IsNullOrWhiteSpace(connectionString);
var defaultPolicy = builder.Configuration.GetSection("Files:DefaultPolicy").Get<ResourceContainerPolicy>();
builder.Services.AddResourcefull(options =>
{
    if (inMemory) options.UseInMemory();
    else options.UseSqlServer(connectionString!);

    // Expiry gives a replaced version a few minutes before it's deleted, so downloads in progress finish. (Versioning
    // would keep replaced versions for restoring, but they count against the quota, and this service doesn't show them.)
    options.UseExpiry()
        .UseContainerPolicies(defaultPolicy);
});

// Signed links. Their keys come from ASP.NET Core Data Protection: with more than one instance, or across restarts,
// persist them somewhere every instance reads, such as a database or a shared directory.
builder.Services.AddResourcefullLinks();
builder.Services.AddDataProtection().SetApplicationName("FileService");

// Resourcefull's exceptions become status codes: a missing file 404, an unmet precondition 412, and an upload the
// policy refuses 413 when it's too large or the quota is full, or 415 when its type isn't accepted.
builder.Services.AddResourcefullExceptionHandler();

// Bearer tokens. In development, make one with: dotnet user-jwts create --project samples/FileService
builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthorization();

// Live: the process answers. Ready: storage answers too, so a load balancer sends it requests.
var health = builder.Services.AddHealthChecks();
if (inMemory) health.AddResourcefullCheck(tags: ["ready"]);
else health.AddResourcefullSqlServerCheck(tags: ["ready"]);

// Traces and metrics, Resourcefull's with ASP.NET Core's, to the OpenTelemetry collector that
// OTEL_EXPORTER_OTLP_ENDPOINT names, such as the .NET Aspire dashboard's.
var telemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddSource(ResourcefullTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter(ResourcefullTelemetry.MeterName));
if (builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is not null) telemetry.UseOtlpExporter();

var app = builder.Build();

if (!inMemory)
{
    // Creates the database or brings its schema up to date. With more than one instance, run this from a deployment
    // step instead, so instances don't migrate at once.
    await app.Services.MigrateResourcefullDatabaseAsync();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

RouteGroupBuilder files = app.MapGroup("/files").RequireAuthorization();

// The signed-in user's files, a page at a time.
files.MapGet("/", async (ClaimsPrincipal user, IResourceServiceClient resources, string? after,
    CancellationToken cancellationToken) =>
{
    IResourceContainerClient container = FilesOf(user, resources);
    try
    {
        var options = new ResourceListOptions { PageSize = 100 };
        ResourcePage page = await container.GetResourcePageAsync(options, after, cancellationToken);
        return Results.Ok(new
        {
            files = page.Resources.Select(file =>
                new { file.Name, file.ContentType, file.ContentLength, file.LastModified }),
            next = page.ContinuationToken
        });
    }
    catch (ArgumentException)
    {
        return Results.BadRequest("The after value isn't one a page of this listing gave.");
    }
});

// How much of their quota the user has used: outside /files, so it can't hide a file named "usage".
app.MapGet("/usage", async (ClaimsPrincipal user, IResourceServiceClient resources,
    CancellationToken cancellationToken) =>
{
    ResourceContainerUsage usage = await FilesOf(user, resources).GetUsageAsync(cancellationToken);
    return Results.Ok(new { stored = usage.StoredSize.ToString(), limit = defaultPolicy?.MaxContainerSize?.ToString() });
}).RequireAuthorization();

// Uploads the request body. If-Match and If-None-Match make it conditional, such as If-None-Match: * to refuse to
// replace a file. The container is created on a user's first upload.
var contentTypes = new FileExtensionContentTypeProvider();
files.MapPut("/{**name}", async (string name, ClaimsPrincipal user, HttpRequest request,
    IResourceServiceClient resources, CancellationToken cancellationToken) =>
{
    // The type comes from the name's extension, not from the client, which could call a program an image; the
    // policy's accepted types are then checked against it, and the download is named and typed to match.
    if (!contentTypes.TryGetContentType(name, out string? contentType))
        return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    IResourceContainerClient container =
        await resources.CreateResourceContainerIfNotExistsAsync(ContainerOf(user), null, cancellationToken);
    var upload = new ResourceUploadOptions { Headers = new ResourceHeaders(contentType, request.ContentLength) };
    return await container.UploadFromRequestAsync(name, request, upload, cancellationToken);
});

// Downloads a file with ETags, Last-Modified and conditional requests, and a part of it for a Range request, so
// videos seek and interrupted downloads resume.
files.MapMethods("/{**name}", ["GET", "HEAD"], (string name, ClaimsPrincipal user, IResourceServiceClient resources) =>
    Results.Resource(FilesOf(user, resources), name));

files.MapDelete("/{**name}", async (string name, ClaimsPrincipal user, IResourceServiceClient resources,
    CancellationToken cancellationToken) =>
    await FilesOf(user, resources).DeleteResourceIfExistsAsync(name, null, cancellationToken)
        ? Results.NoContent()
        : Results.NotFound());

// A link anyone can download the file's current version from, without signing in, for a day.
files.MapPost("/share/{**name}", async (string name, ClaimsPrincipal user, IResourceLinkSigner links,
    HttpRequest request, CancellationToken cancellationToken) =>
{
    string? token = await links.CreateTokenAsync(new ResourceLink(ContainerOf(user), name)
    {
        ExpiresIn = TimeSpan.FromDays(1)
    }, cancellationToken);
    return token is null
        ? Results.NotFound()
        : Results.Ok(new { url = $"{request.Scheme}://{request.Host}{request.PathBase}/shared/{token}" });
});

app.MapMethods("/shared/{token}", ["GET", "HEAD"], (string token, IResourceServiceClient resources) =>
    Results.SharedResource(resources, token));

app.Run();

// One container per user, named for their ID: any ID of up to 123 characters fits the name rules.
static string ContainerOf(ClaimsPrincipal user) =>
    "user-" + (user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The token has no user ID."));

static IResourceContainerClient FilesOf(ClaimsPrincipal user, IResourceServiceClient resources) =>
    resources.GetResourceContainerClient(ContainerOf(user));
