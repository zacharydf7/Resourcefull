using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Looks up a resource's current version and sends it as <see cref="ResourceHttpResult"/> does, with the same headers
/// and conditional requests, and also answers range requests: a <c>GET</c> with one <c>Range: bytes=…</c> gets
/// 206 Partial Content with <c>Content-Range</c>, reading only that range from storage, unless an <c>If-Range</c>
/// doesn't hold, which gets the whole content. A range that starts past the end gets 416 Range Not Satisfiable, and a
/// request for several ranges gets the whole content. Every response has <c>Accept-Ranges: bytes</c>, and a resource
/// that doesn't exist gets 404.
/// <para>Coming in 3.2.</para>
/// </summary>
public sealed class ResourceLookupHttpResult : IResult
{
    internal ResourceLookupHttpResult(IResourceContainerClient container, string resourceName, ResourceResultOptions? options,
        string? versionId = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(resourceName);
        Container = container;
        ResourceName = resourceName;
        Options = options ?? new ResourceResultOptions();
        VersionId = versionId;
    }

    /// <summary>The container the resource is in.</summary>
    public IResourceContainerClient Container { get; }

    /// <summary>The name of the resource this result sends.</summary>
    public string ResourceName { get; }

    /// <summary>How the resource is presented.</summary>
    public ResourceResultOptions Options { get; }

    /// <summary>The version a signed link pins, which this result sends; the current version when <see langword="null"/>.</summary>
    internal string? VersionId { get; }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        HttpRequest request = httpContext.Request;
        HttpResponse response = httpContext.Response;
        CancellationToken cancellationToken = httpContext.RequestAborted;
        response.Headers.AcceptRanges = "bytes";
        RangeItemHeaderValue? requested = RequestedRange(request);

        // The range asked for is opened first, which is right unless it's a suffix, runs past the end, or If-Range
        // doesn't hold; then the version found is opened again for what to send. If that version is deleted in
        // between, it starts over once with the version that's current then, unless it was asked for that version.
        for (int attempt = 0; ; attempt++)
        {
            ResourceDownloadResult? opened = await OpenAsync(VersionId, requested is { From: { } from }
                ? new ResourceRange(from, requested.To < long.MaxValue ? requested.To - from + 1 : null)
                : null, cancellationToken);
            if (opened is null)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            ResourceDownloadResult? download = opened;
            try
            {
                ResourceInfo resource = opened.ResourceInfo;
                int status = ResourceHttpResult.ValidateAndEvaluate(httpContext, resource);
                response.StatusCode = status;
                if (status != StatusCodes.Status200OK) return;

                ResourceRange? wanted = null;
                if (requested is not null && Preconditions.RangeHolds(request, resource))
                {
                    if (!TrySatisfy(requested, opened.TotalLength, out ResourceRange range))
                    {
                        response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                        response.GetTypedHeaders().ContentRange = new ContentRangeHeaderValue(opened.TotalLength);
                        return;
                    }

                    wanted = range;
                }

                if (wanted != opened.Range)
                {
                    download = await OpenAsync(resource.VersionId, wanted, cancellationToken);
                    if (download is null && attempt == 0 && VersionId is null) continue;
                    if (download is null)
                    {
                        response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                }

                if (wanted is { Length: { } length } part)
                {
                    response.StatusCode = StatusCodes.Status206PartialContent;
                    response.GetTypedHeaders().ContentRange =
                        new ContentRangeHeaderValue(part.Offset, part.Offset + length - 1, download.TotalLength);
                }

                await ResourceHttpResult.SendAsync(httpContext, download.Stream, resource, Options);
                return;
            }
            finally
            {
                await opened.Stream.DisposeAsync();
                if (download is not null && download != opened) await download.Stream.DisposeAsync();
            }
        }
    }

    /// <summary>The range, or the whole content when <paramref name="range"/> is null, of a version or the current one.</summary>
    /// <returns>The download; the whole content when the range starts past the end, to answer 416 with its length.</returns>
    private async Task<ResourceDownloadResult?> OpenAsync(string? versionId, ResourceRange? range, CancellationToken cancellationToken)
    {
        if (range is { } part)
        {
            try
            {
                return await Container.DownloadStreamingAsync(ResourceName, versionId, part, cancellationToken);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return await Container.DownloadStreamingAsync(ResourceName, versionId, cancellationToken);
    }

    /// <summary>
    /// The one byte range a <c>GET</c> asks for; null for none, several, another unit or another method, which all get
    /// the whole content.
    /// </summary>
    private static RangeItemHeaderValue? RequestedRange(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) && request.GetTypedHeaders().Range is { Ranges.Count: 1 } range
            && range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            ? range.Ranges.Single()
            : null;

    /// <summary>
    /// The bytes a requested range covers in content of <paramref name="totalLength"/> bytes: <c>from-to</c>, cut at the
    /// end, <c>from-</c> to the end, or the last <c>to</c> bytes for <c>-to</c>.
    /// </summary>
    /// <returns>Whether any of the content is in the range.</returns>
    private static bool TrySatisfy(RangeItemHeaderValue requested, long totalLength, out ResourceRange range)
    {
        long offset = requested.From ?? Math.Max(0, totalLength - requested.To!.Value);
        long end = requested.From is null ? totalLength : Math.Min(requested.To ?? long.MaxValue, totalLength - 1) + 1;
        range = new ResourceRange(offset, end - offset);
        return offset < end;
    }
}
