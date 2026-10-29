# ASP.NET Core

The `Resourcefull.AspNetCore` package serves and accepts files with the status codes and headers HTTP clients
expect, and turns Resourcefull's exceptions into status codes:

```shell
dotnet add package Resourcefull.AspNetCore
```

The endpoints below assume Resourcefull is registered as in [Getting started](../getting-started.md), and leave out
authorization and validation to stay short. In an application, add `RequireAuthorization()` and check which
containers, names, file types and sizes each caller may use.

## Serving downloads

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
  display the file, and `FileName` to offer another name. Display inline only content types your application allows:
  a file uploaded as `text/html` or `image/svg+xml` and shown inline runs as a page of your site.

It answers **conditional requests** without reading the content:

- **304 Not Modified** for an `If-None-Match` that holds the ETag, or an `If-Modified-Since` no earlier than the last
  change;
- **412 Precondition Failed** for an `If-Match` or `If-Unmodified-Since` that doesn't hold.

A `HEAD` request gets the headers alone. The download stream is disposed once the response is sent, and the
request's cancellation token stops the read when the client disconnects. **Range requests** get the whole file (200
rather than 206), because the download is already open; to answer them, see below.

**404** comes from the endpoint: `DownloadStreamingAsync` returns `null` when the container, resource or version
doesn't exist. The catch-all `{**name}` takes names with `/` in them, such as `2026/10/report.pdf`.

### Range requests

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
- **`If-Range`** with the current ETag or `Last-Modified` keeps the range; otherwise the response is the whole file
  (200), so a client resuming a download never joins parts of two versions.
- **416 Range Not Satisfiable**, with `Content-Range: bytes */length`, for a range that starts past the end.
- **The whole file** (200) for a request with several ranges, which HTTP allows. Players and download managers ask
  for one range at a time.
- **`Accept-Ranges: bytes`** on every response, so clients know they can ask.
- **404** when the resource doesn't exist.

Conditional headers are evaluated first, so a matching `If-None-Match` gets 304 even with a `Range`. `TypedResults.Resource`
takes the same arguments.

## Accepting uploads

```csharp
app.MapPut("/files/{container}/{**name}", (string container, string name, HttpRequest request,
    IResourceServiceClient resources, CancellationToken cancellationToken) =>
    resources.GetResourceContainerClient(container).UploadFromRequestAsync(name, request, null, cancellationToken));
```

`UploadFromRequestAsync` streams the request body into storage, so nothing is buffered whatever its size. It stores
the request's `Content-Type`, and passes its `Content-Length` as the declared length, so a truncated body throws
`ResourceContentLengthMismatchException` instead of storing part of a file. Like any `PUT`, it replaces the current
version, whether or not the container keeps versions. It answers:

- **201 Created** when the name had no current version, and **200 OK** when it replaced one, either way with the
  stored `ResourceInfo` as JSON, its `ETag`, and its URI as `Location` when a [URI provider](../uri-providers.md) gives
  one;
- **412 Precondition Failed**, with the current version's `ETag`, when the request's preconditions don't hold.

The **preconditions** are decided by the write itself, so of clients that read the same version and upload changes,
exactly one succeeds (see [Updating only if unchanged](../concurrency.md#updating-only-if-unchanged)):

| Request header | Stores the upload only if |
|---|---|
| `If-Match: "<ETag from a download>"` | the current version still has that content |
| `If-Match: *` | the name has a current version |
| `If-None-Match: *` | the name has no current version |
| `If-None-Match: "<ETag>"` | the current version isn't that one |
| `If-Unmodified-Since` | the current version hasn't changed since then |

`If-Match` compares tags strongly, so a weak tag (`W/"…"`) never matches. Pass `ResourceUploadOptions` for metadata,
other headers to store, or `CreateOnly`; when the request has preconditions, they replace the options' `Conditions`
and `CreateOnly`.

A request body can't seek, so with SQL Server a transient failure partway through an upload isn't retried, and the
client has to send it again.

### From an HTML form

Take an `IFormFile`. ASP.NET Core buffers form files, to disk above 64 KB, before your endpoint runs:

```csharp
app.MapPost("/files/{container}", async (string container, IFormFile file, IResourceServiceClient resources,
    CancellationToken cancellationToken) =>
{
    await using Stream content = file.OpenReadStream();
    ResourceInfo stored = await resources.GetResourceContainerClient(container).UploadAsync(file.FileName, content,
        new ResourceUploadOptions { Headers = new ResourceHeaders(file.ContentType, file.Length) }, cancellationToken);
    return Results.Created(stored.Uri, stored);
}).DisableAntiforgery(); // or validate the form's antiforgery token
```

A form file's stream can seek, so with SQL Server a transient failure partway through retries the upload.

## Large uploads

Kestrel rejects request bodies over about 30 MB with **413 Payload Too Large**, forms included, and form files are also
limited to 128 MB. Raise the limits for the endpoints that take large files:

```csharp
var fiveGigabytes = new RequestSizeLimitAttribute(5L * 1024 * 1024 * 1024);
app.MapPut("/files/{container}/{**name}", UploadAsync).WithMetadata(fiveGigabytes);
app.MapPost("/files/{container}", UploadFormAsync).WithMetadata(fiveGigabytes).DisableAntiforgery();

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 5L * 1024 * 1024 * 1024);
```

Behind IIS or a reverse proxy, raise its limit too (`maxAllowedContentLength`, `client_max_body_size`).

## Turning exceptions into status codes

Register the handler once, rather than catching exceptions in every endpoint:

```csharp
builder.Services.AddResourcefullExceptionHandler();

app.UseExceptionHandler();
```

| Exception | Status |
|---|---|
| `ResourceConflictException` | 409 Conflict |
| `ResourceContainerNotFoundException` | 404 Not Found |
| `ResourceContentLengthMismatchException` | 400 Bad Request |
| `ResourceNameException`, for a name that breaks the [name rules](../containers-and-names.md#names) | 400 Bad Request |
| `ResourcePreconditionFailedException` | 412 Precondition Failed |

The responses are [problem details](https://www.rfc-editor.org/rfc/rfc9457) whose title is the exception's message.
Resourcefull's messages name the resource or container involved, and sometimes the argument or option, but nothing
about your infrastructure, so they're safe to return. Other exceptions, such as a `SqlException`, are left to the default handler, which returns 500 without
details in production. ASP.NET Core removes `ETag` from responses its exception handling writes, so a 412 from the
handler doesn't carry the current version's ETag; the 412 that `UploadFromRequestAsync` returns does.
