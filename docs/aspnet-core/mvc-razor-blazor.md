# MVC, Razor Pages and Blazor

The rest of this section uses minimal APIs, but the same pieces work in controllers, Razor Pages and Blazor, with a
few differences this page covers. Everything here leaves out authorization to stay short; see
[Securing file endpoints](securing-endpoints.md).

## MVC controllers

Controller actions can return the same results as minimal APIs, `Results.Resource` and `UploadFromRequestAsync`'s,
because MVC runs any `IResult` an action returns. The `Results.Resource(container, name)` form, which answers range
requests, is new in 3.2 <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>; with 3.1, download first and pass the
download, as in [Serving downloads](serving-downloads.md):

```csharp
using Microsoft.AspNetCore.Mvc;
using Resourcefull;
using Resourcefull.AspNetCore;

[ApiController]
[Route("files")]
public class FilesController(IResourceServiceClient resources) : ControllerBase
{
    // Downloads, with ETags, conditional requests and ranges.
    [HttpGet("{**name}")]
    public IResult Get(string name) => Results.Resource(resources.GetResourceContainerClient("files"), name);

    // Uploads the request body as it arrives, of any size up to the limit below.
    [HttpPut("{**name}")]
    [RequestSizeLimit(1L * 1024 * 1024 * 1024)]
    public Task<IResult> Put(string name, CancellationToken cancellationToken) =>
        resources.GetResourceContainerClient("files").UploadFromRequestAsync(name, Request, null, cancellationToken);
}
```

For a form upload in an MVC view, take an `IFormFile` and keep `[ValidateAntiForgeryToken]` on the action, as
[Accepting uploads](accepting-uploads.md#from-an-html-form) explains for minimal APIs.

## Razor Pages

A Razor page's handler can't return an `IResult`: ASP.NET Core refuses it at run time, with "Unsupported handler method
return type". From 3.2 <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, the results `TypedResults.Resource` and `TypedResults.SharedResource` return are
action results too, so a handler returns them as it would `File(...)`, conditional requests included, and ranges when
the result looks the resource up, as here:

```csharp
public IActionResult OnGet(string name) =>
    TypedResults.Resource(_resources.GetResourceContainerClient("files"), name);
```

Use `TypedResults`, not `Results`: `Results.Resource` is declared to return `IResult`, which a handler can't return.

With 3.1, serve files from a minimal API endpoint next to your pages, as in [Serving downloads](serving-downloads.md),
and link to it from the page, or run the result yourself in the handler and return an empty result, so the page doesn't
render after it:

```csharp
public async Task<IActionResult> OnGetAsync(string name, CancellationToken cancellationToken)
{
    ResourceDownloadResult? download = await _resources.GetResourceContainerClient("files")
        .DownloadStreamingAsync(name, null, cancellationToken);
    if (download is null) return NotFound();
    await Results.Resource(download).ExecuteAsync(HttpContext);
    return new EmptyResult();
}
```

## Blazor

Where your components run decides how they reach your files.

### Blazor Server

Components with interactive server rendering run on the server, so they can inject `IResourceServiceClient` and use it
as any other code does. (A statically rendered component has no `InputFile` events; give it a form that posts to an
[upload endpoint](accepting-uploads.md#from-an-html-form) instead.)

- **Uploads:** take the file from an `InputFile`, and upload the stream it gives you. `OpenReadStream` refuses files
  over 500 KB unless you pass a larger limit, and the content travels over the page's connection, so for large files an
  [upload endpoint](accepting-uploads.md) the browser sends to directly is faster.

  ```csharp
  private async Task UploadAsync(InputFileChangeEventArgs e)
  {
      await using Stream content = e.File.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024);
      // Browsers report an empty type for files they don't recognize; store none rather than an empty one.
      string? type = string.IsNullOrEmpty(e.File.ContentType) ? null : e.File.ContentType;
      await Resources.GetResourceContainerClient("files").UploadAsync(e.File.Name, content,
          new ResourceUploadOptions { Headers = new ResourceHeaders(type, e.File.Size) });
  }
  ```

- **Downloads:** don't read a file into memory and send it through the page's connection to the browser. Link to a
  download endpoint instead, or to a [shared link](../sharing-links.md), so the file streams straight from storage to
  the browser. Escape each part of the name between `/`s, not the whole name: an escaped `/` doesn't match the
  endpoint's `{**name}`.

  ```razor
  <a href="@($"files/{string.Join('/', name.Split('/').Select(Uri.EscapeDataString))}")">Download</a>
  ```

The [Blazor sample](../../samples/README.md) works this way.

### Blazor WebAssembly

Components run in the browser, which can't reach your database, so they go through your API: the
[endpoints in this section](index.md), called with `HttpClient`.

```csharp
await using Stream content = file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024);
using var body = new StreamContent(content);
if (!string.IsNullOrEmpty(file.ContentType)) body.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
HttpResponseMessage response = await Http.PutAsync($"files/{Uri.EscapeDataString(file.Name)}", body);
```

In the browser, `HttpClient` reads the whole body into memory before it sends it, so size `maxAllowedSize` for what
the browser can hold. Browsers that support it can stream instead: build an `HttpRequestMessage` and call
`request.SetBrowserRequestStreamingEnabled(true)`, which needs HTTP/2 (HTTPS) and a Chromium-based browser.

Download with a link to the endpoint, as for Blazor Server. Components with interactive auto rendering may run in
either place, so treat them as WebAssembly components.
