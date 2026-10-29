using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Net.Http.Headers;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Sends a download: its content with <c>Content-Length</c>, <c>Content-Type</c>, a strong <c>ETag</c> from the
/// content hash, <c>Last-Modified</c> and <c>Content-Disposition</c>. It answers <c>If-None-Match</c> and
/// <c>If-Modified-Since</c> with 304 Not Modified, and <c>If-Match</c> and <c>If-Unmodified-Since</c> that don't hold
/// with 412 Precondition Failed, without reading the content; a <c>HEAD</c> request gets the headers alone. The download
/// stream is disposed once the response is sent or the client disconnects. A range request gets the whole content.
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
        HttpResponse response = httpContext.Response;
        ResponseHeaders headers = response.GetTypedHeaders();

        // Validators go on every response, a 304 included, so a client can revalidate.
        headers.ETag = Preconditions.EntityTagOf(resource);
        headers.LastModified = resource.LastModified;

        int status = Preconditions.Evaluate(httpContext.Request, resource);
        response.StatusCode = status;
        if (status != StatusCodes.Status200OK) return;

        response.ContentType = resource.ContentType ?? "application/octet-stream";
        // The stream knows its length but can't seek, so ASP.NET Core wouldn't set this by itself. It's measured from
        // the stored content, unlike a length an earlier release recorded from its caller.
        response.ContentLength = content.Length;
        var disposition = new ContentDispositionHeaderValue(Options.Inline ? "inline" : "attachment");
        disposition.SetHttpFileName(Options.FileName ?? resource.Name[(resource.Name.LastIndexOf('/') + 1)..]);
        headers.ContentDisposition = disposition;

        if (HttpMethods.IsHead(httpContext.Request.Method)) return;
        await content.CopyToAsync(response.Body, httpContext.RequestAborted);
    }
}
