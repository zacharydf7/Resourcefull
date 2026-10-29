# ASP.NET Core recipes

These minimal API endpoints serve and accept files with the status codes and headers clients expect. They assume
Resourcefull is registered as in [Getting started](../getting-started.md), and leave out authorization and validation
to stay short: in an application, add `RequireAuthorization()` and check which containers, names, file types and sizes
each caller may use.

## Serving downloads

```csharp
using Microsoft.Net.Http.Headers;

app.MapGet("/files/{container}/{name}", async (string container, string name, IResourceServiceClient resources,
    HttpResponse response, CancellationToken cancellationToken) =>
{
    ResourceDownloadResult? download = await resources.GetResourceContainerClient(container)
        .DownloadStreamingAsync(name, null, cancellationToken);
    if (download is null) return Results.NotFound();

    ResourceInfo info = download.ResourceInfo;
    response.ContentLength = download.Stream.Length;
    return Results.Stream(download.Stream, info.ContentType ?? "application/octet-stream",
        fileDownloadName: info.Name, lastModified: info.LastModified,
        entityTag: info.ContentHash is null ? null : new EntityTagHeaderValue($"\"{info.ContentHash}\""));
});
```

- **404** when the container, resource or version doesn't exist: `DownloadStreamingAsync` returns `null`.
- **`Content-Length` has to be set by hand.** The download stream knows its length but can't seek, and ASP.NET Core
  only sets the header for seekable streams, so without that line the response is chunked. Use the stream's `Length`,
  which is measured from the stored content, rather than `ResourceInfo.ContentLength`: versions stored before 3.0 can
  record a length their caller declared, and a wrong `Content-Length` aborts the response.
- **The content hash makes a strong ETag**, so a client that sends `If-None-Match` with it gets **304 Not Modified**
  without the content being read.
- **`fileDownloadName`** adds a `Content-Disposition: attachment` header with the name correctly encoded, whatever
  characters it contains. Leave it out to let browsers display the file inline.
- **`Results.Stream` disposes the stream** once the response is sent, and the request's `CancellationToken` stops the
  read when the client disconnects.
- **Range requests get the whole file** (200 rather than 206), because the stream can't seek.

Names in a route segment can't contain `/`. If yours do, take the name from the query string or a catch-all route
parameter (`{**name}`) instead.

## Accepting uploads

Read the request body directly: nothing is buffered, so the upload streams into storage at any size.

```csharp
app.MapPut("/files/{container}/{name}", async (string container, string name, HttpRequest request,
    IResourceServiceClient resources, CancellationToken cancellationToken) =>
{
    IResourceContainerClient files = resources.GetResourceContainerClient(container);
    ResourceInfo stored = await files.UploadAsync(name, request.Body, new ResourceUploadOptions
    {
        Overwrite = true,
        Headers = new ResourceHeaders(request.ContentType, request.ContentLength)
    }, cancellationToken);
    return Results.Ok(stored);
});
```

Passing the request's `Content-Length` as the declared length makes a truncated body fail with
`ResourceContentLengthMismatchException` instead of storing part of a file.

From an HTML form, take an `IFormFile`. ASP.NET Core buffers form files, to disk above 64 KB, before your endpoint
runs:

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

A form file's stream can seek, so with SQL Server a transient failure partway through retries the upload. A request
body can't, so the client has to send it again.

## Large uploads

Kestrel rejects request bodies over about 30 MB with **413 Payload Too Large**, forms included, and form files are also
limited to 128 MB. Raise the limits for the endpoints that take large files:

```csharp
var fiveGigabytes = new RequestSizeLimitAttribute(5L * 1024 * 1024 * 1024);
app.MapPut("/files/{container}/{name}", UploadAsync).WithMetadata(fiveGigabytes);
app.MapPost("/files/{container}", UploadFormAsync).WithMetadata(fiveGigabytes).DisableAntiforgery();

builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 5L * 1024 * 1024 * 1024);
```

Behind IIS or a reverse proxy, raise its limit too (`maxAllowedContentLength`, `client_max_body_size`).

## Turning exceptions into status codes

Map Resourcefull's exceptions once, rather than in every endpoint:

```csharp
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ResourcefullExceptionHandler>();

app.UseExceptionHandler();

public sealed class ResourcefullExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        int? status = exception switch
        {
            ResourceConflictException => StatusCodes.Status409Conflict,
            ResourceContainerNotFoundException => StatusCodes.Status404NotFound,
            ResourceContentLengthMismatchException => StatusCodes.Status400BadRequest,
            ResourcePreconditionFailedException => StatusCodes.Status412PreconditionFailed,
            // A name that breaks the name rules, or an empty version ID; other ArgumentExceptions are bugs.
            ArgumentException { ParamName: "resourceName" or "resourceContainerName" or "versionId" }
                => StatusCodes.Status400BadRequest,
            _ => null
        };
        if (status is null) return false;

        context.Response.StatusCode = status.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = status, Title = exception.Message }
        });
    }
}
```

Resourcefull's exception messages describe the resource or container involved and nothing about your infrastructure,
so they're safe to return. Leave other exceptions, such as a `SqlException`, to the default handler, which returns 500
without details in production.
