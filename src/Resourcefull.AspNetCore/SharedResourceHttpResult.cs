using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Sends the resource a signed link grants, to whoever holds the link, without authentication: the token is the
/// permission. A valid link's resource is sent as <see cref="ResourceLookupHttpResult"/> sends one, ranges and
/// conditional requests included. An expired link gets 410 Gone; a token this application didn't sign, or a link to a
/// resource or version that's gone, gets 404. Every response carries <c>Referrer-Policy: no-referrer</c> and
/// <c>Cache-Control: private</c> (with <c>no-cache</c> on the file itself), so the link isn't passed on in the
/// <c>Referer</c> header of pages the file links to, nor kept by shared caches.
/// <para>Coming in 3.2.</para>
/// </summary>
public sealed class SharedResourceHttpResult : IResult, IActionResult
{
    internal SharedResourceHttpResult(IResourceServiceClient resources, string token)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(token);
        Resources = resources;
        Token = token;
    }

    /// <summary>The client the resource is read through.</summary>
    public IResourceServiceClient Resources { get; }

    /// <summary>The token from the link.</summary>
    public string Token { get; }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        HttpResponse response = httpContext.Response;
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "private";

        IResourceLinkSigner signer = httpContext.RequestServices.GetService<IResourceLinkSigner>()
            ?? throw new InvalidOperationException(
                $"Results.SharedResource needs signed links added with {nameof(ResourceLinkServiceCollectionExtensions.AddResourcefullLinks)}.");
        ResourceLinkCheck check = signer.Check(Token);
        httpContext.Features.Get<IHttpActivityFeature>()?.Activity.SetTag("resourcefull.link", check.Status switch
        {
            ResourceLinkStatus.Valid => "valid",
            ResourceLinkStatus.Expired => "expired",
            _ => "invalid"
        });

        switch (check)
        {
            case { Status: ResourceLinkStatus.Valid, Link: { } link }:
                await new ResourceLookupHttpResult(Resources.GetResourceContainerClient(link.ContainerName), link.ResourceName,
                    new ResourceResultOptions { Inline = link.Inline }, link.VersionId).ExecuteAsync(httpContext);
                break;
            case { Status: ResourceLinkStatus.Expired }:
                response.StatusCode = StatusCodes.Status410Gone;
                break;
            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    /// <summary>
    /// Runs the result as an MVC action result, so a Razor Pages handler, which can't return an <see cref="IResult"/>,
    /// can return it.
    /// </summary>
    Task IActionResult.ExecuteResultAsync(ActionContext context) => ExecuteAsync(context.HttpContext);
}
