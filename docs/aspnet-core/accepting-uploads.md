# Accepting uploads

Files reach an API in one of two ways: as the whole body of a `PUT` request, which suits scripts and apps, or as a field
in an HTML form. This page covers both, how to let only one of several editors save, and how to accept files larger
than the web server allows by default.

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

## From an HTML form

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
