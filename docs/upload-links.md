# Receiving files through links

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Sometimes you need files from someone who can't sign in to your application: a customer sending photos for an
insurance claim, a new hire sending their documents, a supplier sending an invoice. A **signed upload link** is a URL
that lets whoever has it upload files, without signing in, until a time you choose. You decide where the files go,
how large they may be and what types they may have; the link can't be used for anything else.

Upload links work like [download links](sharing-links.md), and are set up the same way: read that page first if you
haven't. Both kinds are sealed with your application's Data Protection keys, so the advice in
[Shared links in production](sharing-links-in-production.md) applies to upload links too.

## Setting it up

Upload links are part of the `Resourcefull.AspNetCore` package, and use the same registration as download links:

```csharp
using Resourcefull.AspNetCore;

builder.Services.AddResourcefull(options => options.UseSqlServer(connectionString));
builder.Services.AddResourcefullLinks();
```

For a store added with `AddKeyedResourcefull`, pass its name, as for download links: see
[Several stores](several-stores.md#signed-links).

### 1. Add an endpoint that creates links

This is your application's endpoint, so you decide who may ask for a link. Here, a signed-in claims handler gets a
link that lets a customer upload up to 20 MiB per file, of images and PDFs, into the claim's own folder, for a week:

```csharp
app.MapPost("/claims/{claimId:int}/upload-link", (int claimId, IResourceLinkSigner links) =>
{
    string token = links.CreateUploadToken(new ResourceUploadLink("claims")
    {
        NamePrefix = $"{claimId}/",
        MaxSize = 20 * 1024 * 1024,
        AllowedContentTypes = ["image/*", "application/pdf"],
        ExpiresIn = TimeSpan.FromDays(7)
    });

    return Results.Ok(new { url = $"https://example.com/upload/{token}" });
}).RequireAuthorization("ClaimsHandlers");
```

### 2. Add the endpoint that receives uploads

This endpoint must **not** require sign-in, because the people uploading can't sign in. The `{**name}` part of the
route is the file's name, for a link with a prefix; a link to one name leaves it off.

```csharp
app.MapPut("/upload/{token}/{**name}", (string token, string? name, IResourceServiceClient resources) =>
    Results.SharedUpload(resources, token, name));
```

### 3. Upload from the browser

The file is sent as the body of a `PUT` request. From a web page, `fetch` does that, and sends the file's type and
length for you:

```javascript
const file = document.querySelector("input[type=file]").files[0];
const response = await fetch(`${uploadUrl}/${encodeURIComponent(file.name)}`, { method: "PUT", body: file });
```

A plain HTML `<form>` can't send a `PUT`, and the endpoint doesn't unpack the multipart body a form posts: it stores
the request body as the file. Use `fetch`, or any HTTP client that sends the file as the body.

`encodeURIComponent` keeps a name like `scan 1.pdf` intact in the URL. It also encodes `/`, so to upload into a folder
under the prefix, encode each part of the name and keep the slashes between them: with the prefix `1042/`, sending
`scans/form.pdf` stores `1042/scans/form.pdf`.

Serve the page that holds the link with `Referrer-Policy: no-referrer`, so the link isn't passed on to other websites
the page loads from or links to.

## What a link can do

`ResourceUploadLink` describes the link you're creating:

| Property | Default | What it does |
|---|---|---|
| `ResourceName` | | The one name uploads go to. Set this or `NamePrefix`, not both. |
| `NamePrefix` | | What every name starts with: the person uploading picks the rest. Set this or `ResourceName`, not both. |
| `ExpiresIn` | one hour | How long the link works, from when it's created. |
| `MaxSize` | no limit | The most bytes one upload may hold. |
| `AllowedContentTypes` | any type | The types an upload may have, such as `application/pdf`, or `image/*` for every image type. |
| `Mode` | `Create` | `Create` refuses a name that already has a file, so the link can't overwrite anything. `Replace` stores the upload as the name's new version. |

**A link with a prefix can upload any number of files** under it until it expires, each under a name the person
uploading picks. End the prefix with `/`: with `NamePrefix = "1042"`, someone could also upload
`10421/photo.jpg`, into claim 10421's folder.

**With `Create`, nothing is ever overwritten.** That's the safe choice for a link you hand to someone outside your
organization. Use `Replace` for a link to one name that should take a corrected file, such as "upload your signed
contract", and turn on [versioning](versioning.md) if you want to keep what it replaced.

**Your container's [policies](container-policies.md) still apply** on top of the link's limits: the link can narrow
what the container accepts, but never widen it.

The link's own checks come before the file is read, so an upload the link refuses is never read or stored. With
`MaxSize`, the request must say its length (`Content-Length`) up front, which browsers and most HTTP clients do for a
file. The upload is then held to that length: a body cut short stores nothing.

## What people see when they upload

| The request | They get |
|---|---|
| is stored | **201 Created**, or **200 OK** when a `Replace` link replaced a file, with the file's `ETag`. The response has no body, so the person uploading learns nothing about your store beyond what they sent. |
| uses a link that has expired | **410 Gone** |
| uses a link that was changed, made up, or sealed with keys this application no longer has, or a download link's token | **404 Not Found** |
| names a file the link doesn't cover: a name for a link to one name, or no name for a link with a prefix | **404 Not Found** |
| uses a link whose container doesn't exist, or no longer does | **404 Not Found** |
| has no `Content-Length`, for a link with `MaxSize` | **411 Length Required** |
| is larger than `MaxSize` | **413 Content Too Large** |
| has a type the link doesn't allow | **415 Unsupported Media Type** |
| uses a `Create` link whose name already has a file | **409 Conflict** |
| breaks a container policy | **413**, **415**, **409** or **403**, the codes the [exception handler](aspnet-core/status-codes.md) gives |
| names a file the [name rules](names.md) refuse, such as one ending in a space, or has a body shorter or longer than its `Content-Length` | **400 Bad Request** |
| is refused by your [upload inspector](upload-inspection.md) | **422 Unprocessable Content**, with the inspector's reason |

When the store refuses an upload (the row for a missing container, and the rows from 409 Conflict down), the
response has a message of its own rather than the exception's, which would name the container and could say how
full it is. The request's preconditions (`If-Match`, `If-None-Match`) are ignored: the link's `Mode` decides whether
an existing file is replaced.

## Large files: resumable uploads

A `PUT` sends the whole file in one request, so when the connection drops partway, the person uploading starts again
from zero. For large files, such as videos or a 4 GB archive sent over hotel Wi-Fi, map the
[resumable upload endpoints](aspnet-core/resumable-uploads.md) for links too. The page then sends the file with a tus
client such as Uppy or tus-js-client, which carries on from what has arrived after a dropped connection, a reload, or
the next morning:

```csharp
app.MapResourcefullUploadLinks("/upload-links");
```

Like the `PUT` endpoint, these endpoints must **not** require sign-in. A tus client's address for a link is the path
you mapped, followed by `/` and the link's token. So the endpoint that creates links hands out
`https://example.com/upload-links/{token}`, and the page passes that to the client as its `endpoint`:

```javascript
import Uppy from "@uppy/core";
import Dashboard from "@uppy/dashboard";
import Tus from "@uppy/tus";

new Uppy()
  .use(Dashboard, { inline: true, target: "#files" })
  .use(Tus, { endpoint: uploadUrl, chunkSize: 50 * 1024 * 1024 });
```

Each file is checked against the link when its upload starts, before any of it is sent:

- **Its name.** tus clients send the file's name as `filename`: Uppy does that for you, and with tus-js-client, put it
  in `metadata`. A link with a prefix stores the file under the prefix followed by that name. A link to one name stores
  it under that name, whatever the client sends.
- **Its type.** The `filetype` the client sends is checked against `AllowedContentTypes`, and the file is stored with
  it.
- **Its size.** The length the client declares is checked against `MaxSize`. The client can't send more than it
  declared. Unlike a `PUT`, a resumable upload isn't held to ASP.NET Core's limit on how large a request can be, so for
  a link without `MaxSize`, only the container's [policies](container-policies.md) limit it. To limit every upload
  through these endpoints, set their own `MaxSize`, as in the example below.
- **Any file already under its name.** A `Create` link refuses the upload, and a `Replace` link replaces that file
  once the upload finishes.

A link can start as many uploads as it could store files with `PUT`: any number under a prefix. A `Create` link to
one name can start several, but only the first to finish is stored; the others get **409 Conflict** when they finish.

**A link's expiry stops new uploads, not those under way.** An upload started before the link expires can be resumed
and finished after, so a link that expires an hour into a 4 GB upload doesn't waste it. With [`UseExpiry`](expiry.md),
an upload nothing has been appended to for a day is deleted, as any unfinished upload is. Without it, an unfinished
upload stays until it's finished or canceled.

**An upload's URL works for whoever has it**, since the link is the permission: it isn't tied to a signed-in user, as
it is with `MapResourcefullUploads`.

When an upload starts, people see:

| The upload | They get |
|---|---|
| uses a link that has expired | **410 Gone** |
| uses a link that was changed, made up, or sealed with keys this application no longer has, or a download link's token | **404 Not Found** |
| has no `filename`, for a link with a prefix, or has a `filetype` that isn't a media type | **400 Bad Request** |
| is larger than `MaxSize` | **413 Content Too Large** |
| has a type the link doesn't allow, or none, for a link that limits types | **415 Unsupported Media Type** |
| uses a `Create` link whose name already has a file | **409 Conflict** |

After that, an upload through a link works as any resumable upload does: see
[When an upload is finished](aspnet-core/resumable-uploads.md#when-an-upload-is-finished). What the store refuses gets
the same messages as with `PUT`, which don't name the container.

To run code once a file is stored, such as telling the claims handler it arrived, set `OnCompletedAsync`. `MaxSize`
limits every upload through the endpoints, whatever the link. For a store added with `AddKeyedResourcefull`, set
`StoreName`, and add its signed links with `AddResourcefullLinks(storeName)`:

```csharp
app.MapResourcefullUploadLinks("/upload-links", options =>
{
    options.OnCompletedAsync = (context, version) => claims.FileReceivedAsync(version.ResourceName);
    options.MaxSize = 10L * 1024 * 1024 * 1024;     // 10 GiB
});
```

For a page on another origin, add CORS as for the other resumable upload endpoints: see
[Pages on other origins](aspnet-core/resumable-uploads.md#pages-on-other-origins).

## Checking a token yourself

For an endpoint of your own, such as a page that shows the person uploading which types and sizes the link accepts,
`IResourceLinkSigner.CheckUpload` says whether a token is `Valid`, `Expired` or `Invalid`, and what it allows.
`SignedResourceUploadLink.NameFor` gives the name an upload would be stored as.

## Telemetry

Each request that uses an upload link gets `resourcefull.link` on its span: `valid`, `expired` or `invalid`, as a
download link's does. A stored file records the usual `upload` operation. For a resumable upload, only the request
that starts it gets `resourcefull.link`, since that's the one that checks the link. Every request of the upload also gets
`resourcefull.tus`, as described in [Resumable uploads](aspnet-core/resumable-uploads.md#telemetry). See
[Telemetry](telemetry.md).
