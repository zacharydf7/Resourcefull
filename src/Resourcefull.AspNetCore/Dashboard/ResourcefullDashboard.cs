using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Resourcefull.AspNetCore;

/// <summary>
/// What <see cref="ResourcefullDashboardEndpointRouteBuilderExtensions.MapResourcefullDashboard"/> shows.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourcefullDashboardOptions
{
    /// <summary>
    /// The names of the health checks the dashboard runs and shows. Defaults to <c>resourcefull</c>, the name
    /// Resourcefull's health checks are registered under unless you give them another.
    /// </summary>
    public IReadOnlyCollection<string> HealthChecks { get; init; } = ["resourcefull"];

    /// <summary>
    /// Whether the dashboard can change what's stored: upload, delete and restore files and versions, change a version's
    /// expiry, content type and metadata, and create and delete containers. Off by default, so the dashboard only
    /// reads. It's meant for development: map the dashboard with it only where every user the dashboard's authorization
    /// lets in may make those changes. Each change is logged, with the user who made it.
    /// </summary>
    public bool AllowChanges { get; init; }

    /// <summary>
    /// With <see cref="AllowChanges"/>, the path of your application's endpoint that serves download links with
    /// <c>Results.SharedResource</c>, with <c>{token}</c> where the token goes, such as <c>/shared/{token}</c>. The
    /// dashboard then creates download links, and needs <c>AddResourcefullLinks</c>. <see langword="null"/>, the default,
    /// leaves them out.
    /// </summary>
    public string? DownloadLinkPath { get; init; }

    /// <summary>
    /// With <see cref="AllowChanges"/>, the path of your application's endpoint that serves upload links with
    /// <c>Results.SharedUpload</c>, with <c>{token}</c> where the token goes, such as <c>/upload/{token}</c>: the holder
    /// of a link for a name prefix adds <c>/</c> and a file name to the URL. The dashboard then creates upload links, and
    /// needs <c>AddResourcefullLinks</c>. <see langword="null"/>, the default, leaves them out.
    /// </summary>
    public string? UploadLinkPath { get; init; }
}

/// <summary>
/// Maps the Resourcefull dashboard: a page showing the store's health and expiry backlog, and each container's usage
/// against its limits, its largest versions, the versions that expire soonest, and its files and their versions, with
/// the JSON endpoints it reads.
/// <para>Coming in 4.0.</para>
/// </summary>
public static partial class ResourcefullDashboardEndpointRouteBuilderExtensions
{
    /// <summary>How many containers or files one page of the dashboard's JSON endpoints holds.</summary>
    internal const int PageSize = 50;

    /// <summary>How many versions the largest and expiring lists hold.</summary>
    internal const int TopCount = 20;

    /// <summary>How many expired versions waiting for the sweep the dashboard counts; past that, it shows "More than" this many.</summary>
    internal const int ExpiredCounted = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ConcurrentDictionary<string, byte[]> Files = new();

    /// <summary>
    /// Maps the dashboard at <paramref name="pattern"/>: the page at <c>{pattern}/</c>, and JSON endpoints under
    /// <c>{pattern}/api/</c> that can serve as an admin API. They only read, unless
    /// <see cref="ResourcefullDashboardOptions.AllowChanges"/> is set. The dashboard shows container and file names,
    /// which can be personal data, so its endpoints throw <see cref="InvalidOperationException"/> until the group
    /// returned requires authorization, such as with <c>RequireAuthorization("admins")</c>, or a fallback authorization
    /// policy covers it. To show it to anyone, for a tool that only runs locally, call <c>AllowAnonymous()</c> on the
    /// group.
    /// </summary>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">Where to map the dashboard.</param>
    /// <param name="options">What it shows; <see langword="null"/> for the defaults.</param>
    /// <returns>The group the dashboard's endpoints are in, to require authorization on.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourcefullDashboardOptions.DownloadLinkPath"/> or <see cref="ResourcefullDashboardOptions.UploadLinkPath"/>
    /// doesn't start with <c>/</c> or doesn't hold <c>{token}</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Changes and a link path are set, but <c>AddResourcefullLinks</c> wasn't called, so links can't be signed.
    /// </exception>
    public static RouteGroupBuilder MapResourcefullDashboard(this IEndpointRouteBuilder endpoints, string pattern = "/resourcefull",
        ResourcefullDashboardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        options ??= new ResourcefullDashboardOptions();
        ThrowIfBadLinkPath(options.DownloadLinkPath, nameof(options.DownloadLinkPath));
        ThrowIfBadLinkPath(options.UploadLinkPath, nameof(options.UploadLinkPath));
        ThrowIfLinksCantBeSigned(endpoints, options);
        IReadOnlyCollection<string> healthChecks = options.HealthChecks;
        // A provider that doesn't support expiry won't start supporting it while the app runs, so after the first
        // NotSupportedException the dashboard stops asking, rather than recording a failed operation each time the page loads.
        bool expiryUnsupported = false;

        RouteGroupBuilder group = endpoints.MapGroup(pattern);
        group.AddEndpointFilter(ThrowUnlessAuthorizationIsDecided);
        group.AddEndpointFilter(AnswerBadRequestForBadInput);

        group.MapGet("/", (HttpContext httpContext) =>
        {
            Record(httpContext, "page");
            // The page's files and endpoints are relative to it, so it needs the slash to find them.
            PathString path = httpContext.Request.PathBase + httpContext.Request.Path;
            return path.Value!.EndsWith('/')
                ? File("index.html", "text/html; charset=utf-8")
                : Results.Redirect(path + "/" + httpContext.Request.QueryString);
        });
        group.MapGet("/dashboard.js", () => File("dashboard.js", "text/javascript; charset=utf-8"));
        group.MapGet("/dashboard.css", () => File("dashboard.css", "text/css; charset=utf-8"));

        group.MapGet("/api/features", (HttpContext httpContext) =>
        {
            Record(httpContext, "features");
            return Results.Json(new FeaturesView(options.AllowChanges, options.AllowChanges && options.DownloadLinkPath is not null,
                options.AllowChanges && options.UploadLinkPath is not null), Json);
        });

        group.MapGet("/api/health", async (HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "health");
            if (httpContext.RequestServices.GetService<HealthCheckService>() is not { } health)
                return Results.Json(new HealthView(null, []), Json);
            HealthReport report = await health.CheckHealthAsync(r => healthChecks.Contains(r.Name), cancellationToken).ConfigureAwait(false);
            return Results.Json(new HealthView(report.Entries.Count == 0 ? null : report.Status.ToString(),
                [.. report.Entries.Select(e => new HealthCheckView(e.Key, e.Value.Status.ToString(),
                    e.Value.Description ?? e.Value.Exception?.Message, e.Value.Data))]), Json);
        });

        group.MapGet("/api/expired", async (IResourceServiceClient resources, HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            Record(httpContext, "expired");
            if (expiryUnsupported) return Results.Json(new ExpiredView(false, 0, false, null), Json);
            IReadOnlyList<ResourceInfo> expired;
            try
            {
                // One more than it counts, to tell whether there are more.
                expired = await resources.GetExpiredVersionsAsync(ExpiredCounted + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                expiryUnsupported = true;
                return Results.Json(new ExpiredView(false, 0, false, null), Json);
            }
            return Results.Json(new ExpiredView(true, Math.Min(expired.Count, ExpiredCounted), expired.Count > ExpiredCounted,
                expired.Count == 0 ? null : expired[0].ExpiresAt), Json);
        });

        group.MapGet("/api/containers", async (string? prefix, string? continuationToken, IResourceServiceClient resources,
            HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "containers");
            ContainerPage page = await resources.GetContainerPageAsync(new ContainerListOptions { Prefix = prefix, PageSize = PageSize },
                continuationToken, cancellationToken).ConfigureAwait(false);
            var containers = new List<ContainerView>(page.Containers.Count);
            foreach (ContainerInfo container in page.Containers)
                containers.Add(await ViewAsync(resources, container, cancellationToken).ConfigureAwait(false));
            return Results.Json(new PageView<ContainerView>(containers, page.ContinuationToken), Json);
        });

        group.MapGet("/api/container", async (string name, IResourceServiceClient resources, HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            Record(httpContext, "container");
            return await resources.GetContainerInfoAsync(name, cancellationToken).ConfigureAwait(false) is { } container
                ? Results.Json(await ViewAsync(resources, container, cancellationToken).ConfigureAwait(false), Json)
                : Results.NotFound();
        });

        group.MapGet("/api/resources", async (string container, string? prefix, string? continuationToken,
            IResourceServiceClient resources, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "resources");
            if (!await resources.ContainerExistsAsync(container, cancellationToken).ConfigureAwait(false)) return Results.NotFound();
            ResourcePage page = await resources.GetContainerClient(container).GetResourcePageAsync(
                new ResourceListOptions { Prefix = prefix, PageSize = PageSize }, continuationToken, cancellationToken).ConfigureAwait(false);
            return Results.Json(new PageView<ResourceView>([.. page.Resources.Select(View)], page.ContinuationToken), Json);
        });

        group.MapGet("/api/largest", (string container, IResourceServiceClient resources, HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            Record(httpContext, "largest");
            return TopAsync(resources, container, ResourceVersionOrder.LargestFirst, cancellationToken);
        });

        group.MapGet("/api/expiring", (string container, IResourceServiceClient resources, HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            Record(httpContext, "expiring");
            return TopAsync(resources, container, ResourceVersionOrder.ExpiringFirst, cancellationToken);
        });

        group.MapGet("/api/versions", async (string container, string name, IResourceServiceClient resources,
            HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "versions");
            IReadOnlyList<ResourceInfo> versions = await resources.GetContainerClient(container)
                .GetResourceVersionsAsync(name, cancellationToken).ConfigureAwait(false);
            return versions.Count == 0 ? Results.NotFound() : Results.Json(new ListView<ResourceView>([.. versions.Select(View)]), Json);
        });

        if (options.AllowChanges) MapChanges(group, options);
        return group;
    }

    /// <summary>
    /// Runs the endpoint only if whoever mapped the dashboard decided who may see it: by requiring authorization, with a
    /// fallback policy, or by allowing anyone.
    /// </summary>
    private static ValueTask<object?> ThrowUnlessAuthorizationIsDecided(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext httpContext = context.HttpContext;
        EndpointMetadataCollection metadata = httpContext.GetEndpoint()!.Metadata;
        bool decided = metadata.GetMetadata<IAuthorizeData>() is not null
            || metadata.GetMetadata<AuthorizationPolicy>() is not null
            || metadata.GetMetadata<IAuthorizationRequirementData>() is not null
            || metadata.GetMetadata<IAllowAnonymous>() is not null
            || httpContext.RequestServices.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy is not null;
        if (!decided)
        {
            throw new InvalidOperationException("The Resourcefull dashboard shows container and file names, so it runs only " +
                "once you decide who may see it: add RequireAuthorization to MapResourcefullDashboard, such as " +
                "app.MapResourcefullDashboard().RequireAuthorization(\"admins\"), or AllowAnonymous() for a tool that only runs locally.");
        }
        return next(context);
    }

    /// <summary>
    /// Answers 400 for a name the rules refuse, or a continuation token that's malformed or from a listing of another
    /// prefix, rather than letting the caller's mistake become a server error. Other argument exceptions are faults on
    /// the server's side, and propagate.
    /// </summary>
    private static async ValueTask<object?> AnswerBadRequestForBadInput(EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
            when (exception is ResourceNameException || exception.ParamName == "continuationToken")
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: exception.Message);
        }
    }

    /// <summary>Tags the request's span with the dashboard endpoint it called.</summary>
    private static void Record(HttpContext httpContext, string endpoint) =>
        httpContext.Features.Get<IHttpActivityFeature>()?.Activity.SetTag("resourcefull.dashboard", endpoint);

    /// <summary>One of the page's files, which are embedded in this assembly.</summary>
    private static IResult File(string name, string contentType) => Results.Bytes(Files.GetOrAdd(name, static name =>
    {
        using Stream stream = typeof(ResourcefullDashboardEndpointRouteBuilderExtensions).Assembly
            .GetManifestResourceStream("Resourcefull.AspNetCore.Dashboard." + name)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }), contentType);

    private static async Task<IResult> TopAsync(IResourceServiceClient resources, string container, ResourceVersionOrder order,
        CancellationToken cancellationToken)
    {
        if (!await resources.ContainerExistsAsync(container, cancellationToken).ConfigureAwait(false)) return Results.NotFound();
        IReadOnlyList<ResourceInfo> versions = await resources.GetContainerClient(container)
            .GetTopVersionsAsync(order, TopCount, cancellationToken).ConfigureAwait(false);
        return Results.Json(new ListView<ResourceView>([.. versions.Select(View)]), Json);
    }

    private static ResourceView View(ResourceInfo resource) => new(resource.Name, resource.VersionId, resource.IsLatest,
        resource.ContentType, resource.ContentLength, resource.LastModified, resource.ExpiresAt, resource.Metadata);

    private static async Task<ContainerView> ViewAsync(IResourceServiceClient resources, ContainerInfo container,
        CancellationToken cancellationToken)
    {
        IResourceContainerClient client = resources.GetContainerClient(container.Name);
        ResourceContainerUsage usage = await client.GetUsageAsync(cancellationToken).ConfigureAwait(false);
        ResourceContainerPolicy policy = await client.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        return new ContainerView(container.Name, container.Metadata,
            new UsageView(usage.StoredSize.Bytes, usage.ResourceCount, usage.VersionCount),
            new PolicyView(policy.MaxResourceSize?.Bytes, policy.MaxContainerSize?.Bytes, policy.MaxResourceCount,
                policy.ReadOnly == true, policy.AllowedContentTypes, policy.MaxVersions));
    }

    private sealed record HealthView(string? Status, IReadOnlyList<HealthCheckView> Checks);

    private sealed record HealthCheckView(string Name, string Status, string? Description, IReadOnlyDictionary<string, object> Data);

    private sealed record PageView<T>(IReadOnlyList<T> Items, string? ContinuationToken);

    private sealed record ListView<T>(IReadOnlyList<T> Items);

    private sealed record ExpiredView(bool Supported, int Count, bool More, DateTimeOffset? OldestExpiry);

    private sealed record ContainerView(string Name, IDictionary<string, string> Metadata, UsageView Usage, PolicyView Policy);

    private sealed record UsageView(long StoredSize, long ResourceCount, long VersionCount);

    private sealed record PolicyView(long? MaxResourceSize, long? MaxContainerSize, long? MaxResourceCount, bool ReadOnly,
        IReadOnlyCollection<string>? AllowedContentTypes, int? MaxVersions);

    private sealed record ResourceView(string Name, string VersionId, bool IsLatest, string? ContentType, long? ContentLength,
        DateTimeOffset LastModified, DateTimeOffset? ExpiresAt, IReadOnlyDictionary<string, string> Metadata);

    private sealed record FeaturesView(bool Changes, bool DownloadLinks, bool UploadLinks);
}
