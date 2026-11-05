# Resumable uploads from browsers and apps

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

A 2 GB video sent from a phone in a basement, or over hotel Wi-Fi, rarely arrives in one go. With an ordinary upload,
a connection that drops at 80% means starting again from zero. A resumable upload keeps what has arrived, so the
client asks how much is stored and sends only the rest: after a dropped connection, after the browser was closed, or
the next morning.

`MapResourcefullUploads` maps endpoints that speak [tus](https://tus.io/protocols/resumable-upload), the open protocol
for resumable uploads, over Resourcefull's [upload sessions](../uploading-in-parts.md). Front ends then use an existing
tus client and write no upload code of their own:

- in a web page, [Uppy](https://uppy.io), an upload widget with progress bars, pause and resume, or
  [tus-js-client](https://github.com/tus/tus-js-client), if you build your own interface;
- in iOS and Android apps, the tus clients for each, which also resume after the app is closed and reopened;
- in desktop apps and other services, a tus client for their language.

For small files, such as profile pictures, a [plain upload](accepting-uploads.md) is simpler. Reach for resumable
uploads when files are large, connections unreliable, or a proxy in front of your application limits how large a
request can be.

## Setting it up

```csharp
using Resourcefull.AspNetCore;

builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Files")!)
    .UseExpiry());                                  // deletes uploads left unfinished for a day
builder.Services.AddDataProtection();               // seals the upload URLs

var app = builder.Build();

app.MapResourcefullUploads("/uploads", options =>
{
    // Decides where each upload goes. Return null to refuse it.
    options.ResolveAsync = async (context, request) =>
    {
        int claimId = int.Parse(request.Metadata.GetValueOrDefault("claimId", "0"));
        if (!await claims.MayUploadAsync(context.User, claimId)) return null;

        return new ResourceUploadTarget("claims", $"{claimId}/{Guid.NewGuid():N}")
        {
            ContentType = request.Metadata.GetValueOrDefault("filetype"),
            Metadata = { ["original-name"] = request.Metadata.GetValueOrDefault("filename", "file") }
        };
    };

    // Runs once a file is stored, to record it in the app's own database.
    options.OnCompletedAsync = (context, version) =>
        claims.AttachFileAsync(version.ResourceName, version.Metadata["original-name"]);
}).RequireAuthorization();
```

`MapResourcefullUploads` maps everything tus needs at `/uploads`, and returns the group of endpoints, so you add
authorization, [CORS](#pages-on-other-origins) or rate limits to it as to any other. Every built-in provider supports
upload sessions, so this works the same with SQL Server, PostgreSQL, SQLite, the file system, S3 and Azure Blob.

## In the web page

With Uppy, the upload code is a few lines; add its stylesheets as [its documentation](https://uppy.io/docs/) shows:

```javascript
import Uppy from "@uppy/core";
import Dashboard from "@uppy/dashboard";
import Tus from "@uppy/tus";

new Uppy({ meta: { claimId: "1042" } })
  .use(Dashboard, { inline: true, target: "#claim-files" })
  .use(Tus, { endpoint: "/uploads", chunkSize: 50 * 1024 * 1024 });
```

Uppy sends each file's name and type, as `filename` and `filetype`, along with the `meta` you give it. With
tus-js-client, you draw the interface yourself:

```javascript
import { Upload } from "tus-js-client";

const upload = new Upload(file, {
  endpoint: "/uploads",
  chunkSize: 50 * 1024 * 1024,
  retryDelays: [0, 3000, 10000, 30000],
  metadata: { filename: file.name, filetype: file.type, claimId: "1042" },
  onProgress: (sent, total) => (progress.value = sent / total),
  onSuccess: () => showDone(),
  onError: (error) => showError(error)
});

// Carry on with an upload of the same file that didn't finish, such as before the page was reloaded.
const previous = await upload.findPreviousUploads();
if (previous.length > 0) upload.resumeFromPreviousUpload(previous[0]);
upload.start();
```

Both remember unfinished uploads in the browser, so choosing the same file again carries on where it stopped.

## What happens during an upload

| The user | The client sends | Resourcefull |
|---|---|---|
| Adds a 1.8 GB file | `POST /uploads`, with the length and the file's name and type | Calls `ResolveAsync`, checks the container's limits and allowed types, starts an upload session, and answers with the upload's URL |
| Watches the progress bar | `PATCH` requests, one for each 50 MB chunk | Stores each chunk as it streams in |
| Loses the connection at 41% | Nothing, until it's back | Keeps what's stored |
| Reconnects, or comes back later | `HEAD` on the upload's URL, then more `PATCH` requests | Answers how much is stored, and carries on from there |
| Sends the last chunk | The last `PATCH` | Completes the upload: hashes the content, runs the [upload inspector](../upload-inspection.md), makes the version current, then calls `OnCompletedAsync` |

Until the upload completes, nothing shows the file: it isn't listed, and it can't be downloaded. Then it becomes the
name's current version all at once, as a plain upload does.

## When an upload is finished

tus has no call that says "done": an upload is finished when its last byte is stored. Clients take that literally.
tus-js-client, for one, retries a request that fails, and when it resumes an upload whose `HEAD` says every byte is
stored, it tells the user the upload succeeded. When a `HEAD` answers with an error, though, it gives up on that
upload and starts a new one from the beginning, unless the answer is 423 Locked, which it retries. So the endpoints
tell a client an upload finished only once its version is stored and current. When a client resumes an upload they
can't complete just now, they answer 423, so the client tries again:

| What happens | What the client is told |
|---|---|
| The upload starts with a name that already has a current version, and its mode is `Create` | 409 Conflict, before any of the file is sent |
| The upload is larger than `MaxSize` or the container's size limit, or its type isn't allowed | 413 Content Too Large or 415 Unsupported Media Type, before any of it is sent |
| Completing is refused: the name was taken meanwhile, the inspector refused the content, or a policy refused it | The refusal's status code. The upload is canceled, so it can't be completed later. tus-js-client retries a 409, finds the upload gone, and starts a new one, which is refused when it starts if the name is still taken |
| Completing fails for another reason, such as a database that can't be reached | 503 Service Unavailable. The upload is kept. When the client resumes, the endpoints try to complete it again, and while that still fails they answer its `HEAD` with 423 Locked, which the client retries |
| `OnCompletedAsync` throws | 500 Internal Server Error, or the full length when the client's `HEAD` completed the upload as it resumed. The file stays stored, and the exception is logged |
| The client resumes an upload that finished, such as after the page was reloaded | The full length, so it doesn't send the file again |

`OnCompletedAsync` runs after the file is stored, so the file stays even when it throws, and a client that resumes
the upload is told it finished. Save to your database in a way that tolerates a missed call, such as storing the file
first and then saving your row, as [Files and your own data](../files-and-your-data.md#saving-a-new-file) shows.
It can also run twice for one upload, when two requests complete it at once, such as a slow completion and the
client's retry of it, so make it safe to call again for the same version.

## Deciding where uploads go

`ResolveAsync` is required, and it's where your rules go. It gets the request and a `ResourceUploadRequest`: the
upload's `Length`, and the `Metadata` the client sent, decoded. The metadata comes from the client, so treat it as you
would a form field: check it, and don't let it choose a container or a name the user may not write to. Return
`null` to refuse the upload with 403 Forbidden, or a `ResourceUploadTarget`:

| Property | Default | What it does |
|---|---|---|
| `ContainerName`, `ResourceName` | (required) | Where the file is stored. A name of your own, such as a GUID, avoids clashes and odd characters from users' file names |
| `Mode` | `Create` | `Create` refuses a name that has a current version, when the upload starts. `Replace` replaces it when the upload completes |
| `ContentType` | none | The version's content type. A type the container doesn't allow is refused when the upload starts |
| `Metadata` | empty | Metadata to store with the version, such as the file's original name |
| `ExpiresAt`, `RetainUntil`, `LegalHold` | none | As for any upload: see [Expiring resources](../expiry.md) and [Deleting safely](../deleting-safely.md) |

The container's [policies](../container-policies.md), the [name rules](../names.md) and the
[upload inspector](../upload-inspection.md) apply as to any other upload.

## Limits

An upload's size is checked when it starts, before any of it is sent: against `MaxSize`, if you set one (the
endpoints also send it to clients in `Tus-Max-Size`), and against the container's `MaxResourceSize` and
`MaxContainerSize`. As content arrives, the container's limits are checked again.

No request stores more than its upload has left: a request that says it carries more is refused with 413 Content Too
Large before any of it is read, and one that sends more without saying so is cut off at the upload's length, and
refused the same way. So the endpoints lift ASP.NET Core's limit on how large a request's body can be, for their own
requests only. A server in front of your application may still limit it: IIS
(`maxAllowedContentLength`), nginx (`client_max_body_size`, 1 MB by default) or a CDN. Rather than raise those, have
clients send the file in chunks smaller than the smallest limit, with `chunkSize` as in the examples above. Chunks
also keep each request short, which matters behind hosts and load balancers that end long requests.

## Pages on other origins

When the page is served from a different origin than the endpoints, the browser needs CORS, and tus clients need to read
headers that browsers hide by default, such as `Location` and `Upload-Offset`. `WithResourcefullUploadHeaders` adds
what the endpoints use to a policy:

```csharp
builder.Services.AddCors(cors => cors.AddPolicy("uploads", policy => policy
    .WithOrigins("https://app.example.com")
    .WithResourcefullUploadHeaders()));

app.UseCors();
app.MapResourcefullUploads("/uploads", ConfigureUploads).RequireCors("uploads").RequireAuthorization();
```

A missing exposed header is the most common reason a tus client fails in the browser while working in tests.

## Who can use an upload's URL

An upload's URL holds a token sealed with ASP.NET Core Data Protection, as [signed links](../sharing-links.md) are. It
names the upload, so a changed or made-up URL gets 404, and it gives away no container or file names. It works only
with endpoints mapped with the same pattern string for the same store. So when an app maps two sets with patterns of
their own, such as `/avatars/uploads` and `/documents/uploads`, one set's uploads can't be finished through the
other's, whose `ResolveAsync` and `OnCompletedAsync` expect different files. The pattern is only the string passed to
`MapResourcefullUploads`, not a route group's prefix, so two sets mapped at `/uploads` in different route groups share
their URLs: give each set a pattern of its own. When the
request that started the upload was signed in, the token also holds a hash of the user's ID, from the
`NameIdentifier` claim, `sub` or the user's name, and the URL then works only for that user.

Every instance of your application must share the Data Protection keys, kept where they survive a restart, or an
upload started on one instance can't be resumed on another, nor after a deploy. See
[Keeping links working](../sharing-links-in-production.md#keeping-links-working-data-protection-keys): upload URLs need
the same.

What an upload has stored is kept in the store, not in the web server's memory, so with the keys shared, any instance
can take the next chunk: there's no need for sticky sessions.

## Unfinished uploads

With [`UseExpiry`](../expiry.md), the expiry sweep deletes an upload nothing was appended to for a day, and the
endpoints say when in `Upload-Expires`, so a client knows whether it can still resume. Without `UseExpiry`, nothing
deletes unfinished uploads, and the endpoints don't send `Upload-Expires`. A client can also cancel an upload with
`DELETE` on its URL, which deletes what it stored; tus-js-client does that with `upload.abort(true)`.

## People who can't sign in

To take resumable uploads from people who can't sign in, such as customers sending claim photos, hand them an
[upload link](../upload-links.md), and map the endpoints for links with `MapResourcefullUploadLinks`. The link then
decides where each upload goes, in place of `ResolveAsync`. See
[Large files: resumable uploads](../upload-links.md#large-files-resumable-uploads).

## Several stores

For a store added with [`AddKeyedResourcefull`](../several-stores.md), set `StoreName`, and map the endpoints once for
each store that takes uploads:

```csharp
app.MapResourcefullUploads("/archive/uploads", options =>
{
    options.StoreName = "archive";
    options.ResolveAsync = ResolveArchiveUpload;
}).RequireAuthorization();
```

An upload's URL works only with endpoints mapped with the same pattern for the store it was started in. Changing
the pattern the endpoints are mapped at makes the URLs of unfinished uploads stop working, as changing the Data
Protection keys does.

## What's supported

The endpoints speak tus 1.0 with these extensions:

| Extension | What it does | Supported |
|---|---|---|
| creation | `POST` starts an upload | Yes |
| creation-with-upload | The `POST` can carry the first chunk, saving a round trip | Yes |
| termination | `DELETE` cancels an upload | Yes |
| expiration | `Upload-Expires` says when an unfinished upload is deleted | Yes, with `UseExpiry` |
| creation-defer-length | Starting an upload without knowing its length | No: send `Upload-Length` |
| checksum | A checksum for each chunk | No: HTTPS protects each chunk, and completing hashes the whole file |
| concatenation | Sending parts of one file at once, over several connections | No |

They also take `X-HTTP-Method-Override`, for clients that can't send `PATCH` or `DELETE`.

## Telemetry

Each request's span gets `resourcefull.tus`, naming what it did: `options`, `create`, `resume`, `append`,
`complete` (a request that completed the upload) or `cancel`. For a [named store](../several-stores.md), it also gets
`resourcefull.store`. The upload session calls record their own spans, and count bytes in
`resourcefull.client.appended` and `resourcefull.client.uploaded`, as in
[Uploading in parts](../uploading-in-parts.md#telemetry). A completion that fails for a reason other than a refusal is
logged as a warning, and an exception from `OnCompletedAsync` as an error, both under
`Resourcefull.AspNetCore.Uploads`. See [Telemetry](../telemetry.md).

## Troubleshooting

**The client fails in the browser, but not in tests.** The page is on another origin, and CORS doesn't expose the
headers it reads. See [Pages on other origins](#pages-on-other-origins).

**Uploads fail with 413 partway through.** A server in front of your application limits request sizes. Set the
client's `chunkSize` below its limit. See [Limits](#limits).

**Every resumed upload gets 404 after a deploy.** The Data Protection keys changed. Keep them where every instance
can read them, and where they survive a restart. See [Who can use an upload's URL](#who-can-use-an-uploads-url).

**An upload gets 409 when it starts.** Its target's mode is `Create`, and the name already has a current version.
Choose a name of your own, such as a GUID, or set `Mode = ResourceWriteMode.Replace`.

**Uploads get 403.** `ResolveAsync` returned `null` for them.
