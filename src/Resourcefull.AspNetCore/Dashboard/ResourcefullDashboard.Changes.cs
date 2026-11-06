using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Resourcefull.AspNetCore;

public static partial class ResourcefullDashboardEndpointRouteBuilderExtensions
{
    /// <summary>The header every change must carry, which the page sends and another site's page can't.</summary>
    internal const string ChangeHeader = "X-Resourcefull-Dashboard";

    /// <summary>The longest a link the dashboard creates may work for.</summary>
    internal static readonly TimeSpan MaxLinkLifetime = TimeSpan.FromDays(7);

    private static void ThrowIfBadLinkPath(string? path, string name)
    {
        if (path is not null && (!path.StartsWith('/') || !path.Contains("{token}", StringComparison.Ordinal)))
        {
            throw new ArgumentException($"{name} must start with / and hold {{token}}, where the link's token goes, such as /shared/{{token}}.",
                name);
        }
    }

    /// <summary>Throws now, when the dashboard is mapped, if it's told to create links the app can't sign.</summary>
    private static void ThrowIfLinksCantBeSigned(IEndpointRouteBuilder endpoints, ResourcefullDashboardOptions options)
    {
        if (options.AllowChanges && (options.DownloadLinkPath ?? options.UploadLinkPath) is not null
            && endpoints.ServiceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(IResourceLinkSigner)) == false)
        {
            throw new InvalidOperationException(NoSigner);
        }
    }

    private const string NoSigner = "The dashboard creates links only with AddResourcefullLinks: call builder.Services.AddResourcefullLinks().";

    /// <summary>Maps the endpoints that change what's stored, which <see cref="ResourcefullDashboardOptions.AllowChanges"/> turns on.</summary>
    private static void MapChanges(RouteGroupBuilder group, ResourcefullDashboardOptions options)
    {
        RouteGroupBuilder changes = group.MapGroup("/api");
        changes.AddEndpointFilter(RefuseForgedChanges);
        changes.AddEndpointFilter(AnswerStoreRefusals);

        changes.MapDelete("/resource", async (string container, string name, string? versionId, IResourceServiceClient resources,
            HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "delete");
            if (!await resources.GetContainerClient(container).DeleteResourceIfExistsAsync(name, versionId, null, cancellationToken)
                .ConfigureAwait(false)) return Results.NotFound();
            Audit(httpContext, "delete", container, name, versionId);
            return Results.NoContent();
        });

        changes.MapPost("/restore", async (string container, string name, string versionId, IResourceServiceClient resources,
            HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "restore");
            if (string.IsNullOrEmpty(versionId)) return BadInput("Give the versionId of the version to restore.");
            if (await resources.GetContainerClient(container).RestoreVersionAsync(name, versionId, cancellationToken)
                .ConfigureAwait(false) is not { } restored) return Results.NotFound();
            Audit(httpContext, "restore", container, name, versionId);
            return Results.Json(View(restored), Json);
        });

        changes.MapPut("/expiry", async (string container, string name, string? versionId, ExpiryChange change,
            IResourceServiceClient resources, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "set_expiry");
            if (!await resources.GetContainerClient(container).SetExpiryAsync(name, versionId, change.ExpiresAt, cancellationToken)
                .ConfigureAwait(false)) return Results.NotFound();
            Audit(httpContext, "set_expiry", container, name, versionId);
            return Results.NoContent();
        });

        changes.MapPut("/content-type", async (string container, string name, string? versionId, ContentTypeChange change,
            IResourceServiceClient resources, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "set_content_type");
            if (!await resources.GetContainerClient(container).SetContentTypeAsync(name, versionId, change.ContentType, cancellationToken)
                .ConfigureAwait(false)) return Results.NotFound();
            Audit(httpContext, "set_content_type", container, name, versionId);
            return Results.NoContent();
        });

        changes.MapPut("/metadata", async (string container, string name, string? versionId, MetadataChange change,
            IResourceServiceClient resources, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "set_metadata");
            if (!await resources.GetContainerClient(container).SetResourceMetadataAsync(name, versionId, change.Metadata ?? [],
                cancellationToken).ConfigureAwait(false)) return Results.NotFound();
            Audit(httpContext, "set_metadata", container, name, versionId);
            return Results.NoContent();
        });

        // The file is the request body, as an upload link takes it, so a large one isn't buffered.
        changes.MapPut("/upload", async (string container, string name, bool? replace, IResourceServiceClient resources,
            HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            Record(httpContext, "upload");
            HttpRequest request = httpContext.Request;
            ResourceInfo stored = await resources.GetContainerClient(container).UploadAsync(name, request.Body, new ResourceUploadOptions
            {
                Mode = replace == true ? ResourceWriteMode.Replace : ResourceWriteMode.Create,
                Headers = new ResourceHeaders
                {
                    ContentType = request.ContentType,
                    ContentLength = request.ContentLength
                }
            }, cancellationToken).ConfigureAwait(false);
            Audit(httpContext, "upload", container, name, stored.VersionId);
            return Results.Json(View(stored), Json, statusCode: StatusCodes.Status201Created);
        });

        changes.MapPost("/containers", async (ContainerChange change, IResourceServiceClient resources, HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            Record(httpContext, "create_container");
            if (string.IsNullOrEmpty(change.Name)) return BadInput("Give the new container's name.");
            await resources.CreateContainerAsync(change.Name, null, cancellationToken).ConfigureAwait(false);
            Audit(httpContext, "create_container", change.Name, null, null);
            return Results.StatusCode(StatusCodes.Status201Created);
        });

        changes.MapDelete("/container", async (string name, IResourceServiceClient resources, HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            Record(httpContext, "delete_container");
            if (!await resources.DeleteContainerIfExistsAsync(name, cancellationToken).ConfigureAwait(false)) return Results.NotFound();
            Audit(httpContext, "delete_container", name, null, null);
            return Results.NoContent();
        });

        if (options.DownloadLinkPath is { } downloadPath)
        {
            changes.MapPost("/download-link", async (string container, string name, string? versionId, LinkChange change,
                HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                Record(httpContext, "download_link");
                if (LinkLifetime(change.ExpiresInMinutes) is not { } expiresIn) return BadLifetime();
                IResourceLinkSigner signer = Signer(httpContext);
                string? token = await signer.CreateTokenAsync(new ResourceLink(container, name)
                {
                    VersionId = versionId, ExpiresIn = expiresIn
                }, cancellationToken).ConfigureAwait(false);
                if (token is null) return Results.NotFound();
                Audit(httpContext, "download_link", container, name, versionId);
                return Results.Json(new LinkView(LinkUrl(httpContext, downloadPath, token), signer.Check(token).Link!.ExpiresAt), Json);
            });
        }

        if (options.UploadLinkPath is { } uploadPath)
        {
            changes.MapPost("/upload-link", async (string container, UploadLinkChange change, IResourceServiceClient resources,
                HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                Record(httpContext, "upload_link");
                if (LinkLifetime(change.ExpiresInMinutes) is not { } expiresIn) return BadLifetime();
                if (!await resources.ContainerExistsAsync(container, cancellationToken).ConfigureAwait(false)) return Results.NotFound();
                IResourceLinkSigner signer = Signer(httpContext);
                string token;
                try
                {
                    token = signer.CreateUploadToken(new ResourceUploadLink(container)
                    {
                        ResourceName = change.Name, NamePrefix = change.NamePrefix, ExpiresIn = expiresIn,
                        Mode = change.Replace ? ResourceWriteMode.Replace : ResourceWriteMode.Create
                    });
                }
                catch (ArgumentException exception)
                {
                    // The signer refuses a link with both a name and a prefix, or neither, or one that breaks the name rules.
                    return BadInput(exception.Message);
                }
                Audit(httpContext, "upload_link", container, change.Name ?? change.NamePrefix, null);
                return Results.Json(new LinkView(LinkUrl(httpContext, uploadPath, token), signer.CheckUpload(token).Link!.ExpiresAt), Json);
            });
        }
    }

    /// <summary>
    /// Refuses a change another site's page could have made a signed-in user's browser send. A browser sends a header
    /// of a page's own choosing to another site only after asking that site, which the dashboard never allows, so the
    /// header the page sends shows the request came from it. A request whose browser reports, in Sec-Fetch-Site, any
    /// origin but the dashboard's own is refused too.
    /// </summary>
    private static ValueTask<object?> RefuseForgedChanges(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext httpContext = context.HttpContext;
        HttpRequest request = httpContext.Request;
        string? site = request.Headers["Sec-Fetch-Site"];
        if (request.Headers.ContainsKey(ChangeHeader) && site is null or "same-origin") return next(context);

        // A refused change may be an attack, so it's logged with the changes the dashboard makes.
        LogRefused(Logger(httpContext), request.Method, request.Path + request.QueryString, UserOf(httpContext), site ?? "not sent");
        return ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
            title: $"A dashboard change needs the {ChangeHeader} header, sent from the dashboard's own page."));
    }

    /// <summary>
    /// Answers what the store refuses, such as a conflict or a read-only container, with the status code
    /// <see cref="ResourcefullExceptionHandler"/> gives it, so the page can say why.
    /// </summary>
    private static async ValueTask<object?> AnswerStoreRefusals(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (ResourcefullException exception) when (ResourcefullExceptionHandler.StatusCodeOf(exception) is { } status)
        {
            return Results.Problem(statusCode: status, title: exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: exception.Message);
        }
    }

    private static IResourceLinkSigner Signer(HttpContext httpContext) =>
        httpContext.RequestServices.GetService<IResourceLinkSigner>() ?? throw new InvalidOperationException(NoSigner);

    /// <summary>How long a link works, an hour unless the page asks otherwise, or <see langword="null"/> outside the limits.</summary>
    private static TimeSpan? LinkLifetime(int? minutes)
    {
        TimeSpan lifetime = TimeSpan.FromMinutes(minutes ?? 60);
        return lifetime > TimeSpan.Zero && lifetime <= MaxLinkLifetime ? lifetime : null;
    }

    private static IResult BadLifetime() => BadInput(
        $"expiresInMinutes must be between 1 and {MaxLinkLifetime.TotalMinutes:0} ({MaxLinkLifetime.TotalDays:0} days).");

    private static IResult BadInput(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);


    /// <summary>The link's URL: the app's path with the token in it, under the app's path base, from the request's host.</summary>
    private static string LinkUrl(HttpContext httpContext, string path, string token)
    {
        HttpRequest request = httpContext.Request;
        return $"{request.Scheme}://{request.Host}{request.PathBase}{path.Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal)}";
    }

    /// <summary>Logs a change, with the user who made it, as the dashboard's audit trail.</summary>
    private static void Audit(HttpContext httpContext, string action, string container, string? name, string? versionId) =>
        LogChange(Logger(httpContext), action, UserOf(httpContext), container, name, versionId);

    private static ILogger Logger(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Resourcefull.AspNetCore.Dashboard");

    /// <summary>
    /// Who sent the request, for the log: the user's name, or the ID a token without a name claim carries, since many do.
    /// </summary>
    private static string UserOf(HttpContext httpContext)
    {
        ClaimsPrincipal user = httpContext.User;
        if (user.Identity?.IsAuthenticated != true) return "an anonymous user";
        return user.Identity.Name ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "a signed-in user without a name or ID";
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Dashboard change {Action} by {User} in container {ContainerName}, resource {ResourceName}, version {VersionId}.")]
    private static partial void LogChange(ILogger logger, string action, string user, string containerName, string? resourceName,
        string? versionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Dashboard refused a change, {Method} {Path} by {User}, without its header or from another origin (Sec-Fetch-Site: {Site}).")]
    private static partial void LogRefused(ILogger logger, string method, string path, string user, string site);

    private sealed record ExpiryChange(DateTimeOffset? ExpiresAt);

    private sealed record ContentTypeChange(string? ContentType);

    private sealed record MetadataChange(Dictionary<string, string>? Metadata);

    private sealed record ContainerChange(string Name);

    private sealed record LinkChange(int? ExpiresInMinutes);

    private sealed record UploadLinkChange(string? Name, string? NamePrefix, int? ExpiresInMinutes, bool Replace);

    private sealed record LinkView(string Url, DateTimeOffset ExpiresAt);
}
