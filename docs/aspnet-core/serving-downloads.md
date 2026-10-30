# Serving downloads

An endpoint that serves files should do more than copy bytes: send the length, type and file name, let browsers
reuse what hasn't changed without letting shared caches keep anyone's files, and let video players and download
managers ask for part of a file. `Results.Resource`
does all of that from a download.

> [!NOTE]
> This endpoint leaves out authorization to stay short. Before you use it, see
> [Securing file endpoints](securing-endpoints.md).

```csharp
using Resourcefull.AspNetCore;

app.MapMethods("/files/{container}/{**name}", ["GET", "HEAD"], async (string container, string name,
    IResourceServiceClient resources, CancellationToken cancellationToken) =>
{
    ResourceDownloadResult? download = await resources.GetResourceContainerClient(container)
        .DownloadStreamingAsync(name, null, cancellationToken);
    return download is null ? Results.NotFound() : Results.Resource(download);
});
```

`Results.Resource` (or `TypedResults.Resource`, for typed results) sends the download with:

- **`Content-Length`**, measured from the stored content, so the response isn't chunked;
- **`Content-Type`**, or `application/octet-stream` when none was stored;
- **a strong `ETag`** from the content hash, and **`Last-Modified`**;
- **`Content-Disposition: attachment`**, with the part of the name after its last `/` as the file name, encoded
  correctly whatever characters it contains. Pass `new ResourceResultOptions { Inline = true }` to let browsers
  display the file, and `FileName` to offer another name. Only types that are safe to display are shown inline
  <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>: common image formats (PNG, JPEG, GIF, WebP,
  AVIF and BMP; not SVG), PDF, audio, video and plain text (`ResourceResultOptions.DefaultInlineContentTypes`). Anything else
  is still offered as a download, because a file uploaded as `text/html` or `image/svg+xml` and shown inline would run
  as a page of your site. `InlineContentTypes` changes the list;
- **`Cache-Control: private, no-cache`** <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>: the
  user's browser may keep a copy, but checks with your application before reusing it, which the `ETag` makes cheap,
  and shared caches such as CDNs and proxies don't keep it, so one user's file is never served to another. For public
  files whose URL names a version, so their content never changes, set `CacheControl` to something like
  `public, max-age=31536000, immutable`;
- **`X-Content-Type-Options: nosniff`** <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, so
  browsers take the type as given rather than guessing from the content.

It answers **conditional requests** without reading the content:

- **304 Not Modified** for an `If-None-Match` that holds the ETag, or an `If-Modified-Since` no earlier than the last
  change;
- **412 Precondition Failed** for an `If-Match` or `If-Unmodified-Since` that doesn't hold.

A `HEAD` request gets the headers alone. The download stream is disposed once the response is sent, and the
request's cancellation token stops the read when the client disconnects. **Range requests** get the whole file (200
rather than 206), because the download is already open; to answer them, see below.

**404** comes from the endpoint: `DownloadStreamingAsync` returns `null` when the container, resource or version
doesn't exist. The catch-all `{**name}` takes names with `/` in them, such as `2026/10/report.pdf`.

## Range requests

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

Video players seek, and download managers resume, by asking for a range of bytes. To answer them, pass the container
and the name instead of a download, and the result looks the resource up itself:

```csharp
app.MapMethods("/files/{container}/{**name}", ["GET", "HEAD"], (string container, string name,
    IResourceServiceClient resources) =>
    Results.Resource(resources.GetResourceContainerClient(container), name));
```

It sends the same headers and answers the same conditional requests as above, and also:

- **206 Partial Content** with `Content-Range` for one `Range: bytes=…`, reading only that range from storage. The
  forms `bytes=0-499`, `bytes=500-` and `bytes=-500` (the last 500 bytes) all work, and a range that runs past the end
  is cut there.
- **`If-Range`** with the ETag or `Last-Modified` of the version sent keeps the range; otherwise the response is the
  whole file (200), so a client resuming a download never joins parts of two versions.
- **416 Range Not Satisfiable**, with `Content-Range: bytes */length`, for a range that starts past the end.
- **The whole file** (200) for a request with several ranges, which HTTP allows. Players and download managers ask
  for one range at a time.
- **`Accept-Ranges: bytes`** on every response, so clients know they can ask.
- **404** when the resource doesn't exist.

To serve an older version with ranges, pass its ID as `versionId`, for example from the query string; a version that
doesn't exist gets 404:

```csharp
app.MapMethods("/files/{container}/{**name}", ["GET", "HEAD"], (string container, string name, string? versionId,
    IResourceServiceClient resources) =>
    Results.Resource(resources.GetResourceContainerClient(container), name, versionId));
```

Conditional headers are evaluated first, so a matching `If-None-Match` gets 304 even with a `Range`. `TypedResults.Resource`
takes the same arguments.
