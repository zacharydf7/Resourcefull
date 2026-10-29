using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Sends a download: its content with <c>Content-Length</c>, <c>Content-Type</c>, a strong <c>ETag</c> from the
/// content hash, <c>Last-Modified</c> and <c>Content-Disposition</c>. It answers <c>If-None-Match</c> and
/// <c>If-Modified-Since</c> with 304 Not Modified, and <c>If-Match</c> and <c>If-Unmodified-Since</c> that don't hold
/// with 412 Precondition Failed, without reading the content; a <c>HEAD</c> request gets the headers alone. The download
/// stream is disposed once the response is sent or the client disconnects. A range request gets the whole content: for
/// ranges, use <see cref="ResourceLookupHttpResult"/>, which opens the download itself.
/// </summary>
public sealed class ResourceHttpResult : IResult
{
    internal ResourceHttpResult(ResourceDownloadResult download, ResourceResultOptions? options)
    {
        ArgumentNullException.ThrowIfNull(download);
        Download = download;
        Options = options ?? new ResourceResultOptions();
    }

    /// <summary>The download this result sends.</summary>
    public ResourceDownloadResult Download { get; }

    /// <summary>How the download is presented.</summary>
    public ResourceResultOptions Options { get; }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        await using Stream content = Download.Stream;
        ResourceInfo resource = Download.ResourceInfo;

        int status = ValidateAndEvaluate(httpContext, resource);
        httpContext.Response.StatusCode = status;
        if (status != StatusCodes.Status200OK) return;

        await SendAsync(httpContext, content, resource, Options);
    }

    /// <summary>
    /// Sets the validators, which go on every response, a 304 included, so a client can revalidate; then evaluates
    /// and records the request's preconditions.
    /// </summary>
    /// <returns>200 to go ahead, 304 Not Modified, or 412 Precondition Failed.</returns>
    internal static int ValidateAndEvaluate(HttpContext httpContext, ResourceInfo resource)
    {
        ResponseHeaders headers = httpContext.Response.GetTypedHeaders();
        headers.ETag = Preconditions.EntityTagOf(resource);
        headers.LastModified = resource.LastModified;

        int status = Preconditions.Evaluate(httpContext.Request, resource);
        if (Preconditions.Any(httpContext.Request))
            Preconditions.Record(httpContext, status switch { StatusCodes.Status304NotModified => "not_modified", StatusCodes.Status200OK => "met", _ => "failed" });
        return status;
    }

    /// <summary>Sends the content's headers, and the content itself unless the request is a <c>HEAD</c>.</summary>
    internal static async Task SendAsync(HttpContext httpContext, Stream content, ResourceInfo resource, ResourceResultOptions options)
    {
        HttpResponse response = httpContext.Response;
        response.ContentType = resource.ContentType ?? "application/octet-stream";
        // The stream knows its length but can't seek, so ASP.NET Core wouldn't set this by itself. It's measured from
        // the stored content, unlike a length an earlier release recorded from its caller.
        response.ContentLength = content.Length;
        var disposition = new ContentDispositionHeaderValue(options.Inline ? "inline" : "attachment");
        disposition.SetHttpFileName(options.FileName ?? resource.Name[(resource.Name.LastIndexOf('/') + 1)..]);
        response.GetTypedHeaders().ContentDisposition = disposition;

        if (HttpMethods.IsHead(httpContext.Request.Method)) return;
        await content.CopyToAsync(response.Body, httpContext.RequestAborted);
    }
}
