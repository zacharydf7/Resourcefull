# Securing file endpoints

The examples in this section map endpoints that anyone can call, to keep them short. A real application has to decide,
for every request, who's asking and which files they may read or write. Resourcefull stores and serves files; it
doesn't know your users, so it can't make that decision for you. This page shows how to make it, step by step, with
no security background needed.

The short version: **sign people in, pick the container from who they are rather than from the URL, choose file names
yourself, limit what can be uploaded, and keep links and names out of your logs.**

## 1. Require sign-in

An endpoint without authorization answers anyone on the internet. Add `RequireAuthorization()` to every endpoint that
reads or writes files, so only signed-in users reach it:

```csharp
app.MapGet("/files/{**name}", ...).RequireAuthorization();
app.MapPut("/files/{**name}", ...).RequireAuthorization();
```

Endpoints for administrators, such as [exporting a container](../backup-and-move.md), take a stricter policy:
`RequireAuthorization("admins")`, where `admins` is a policy you define with `AddAuthorization`. The one exception is
the endpoint that serves [shared links](../sharing-links.md): the link itself is the permission, so it must stay open.

## 2. Pick the container from who's asking

Signing in says who someone is, not which files are theirs. If the container comes from the URL, as in
`/files/{container}/{**name}`, a signed-in user can change the URL and read someone else's container.

Instead, work out the container from the signed-in user. With one container per user:

```csharp
using System.Security.Claims;

app.MapGet("/files/{**name}", async (string name, ClaimsPrincipal user, IResourceServiceClient resources,
    CancellationToken cancellationToken) =>
{
    // The container belongs to whoever signed in; nothing in the URL can point it elsewhere.
    string container = $"user-{user.FindFirstValue(ClaimTypes.NameIdentifier)}";
    return Results.Resource(resources.GetResourceContainerClient(container), name);
}).RequireAuthorization();
```

When files are shared by a team, a customer account or a project, keep the membership in your own database and check
it before touching the container:

```csharp
app.MapGet("/projects/{projectId}/files/{**name}", async (string projectId, string name, ClaimsPrincipal user,
    IProjectMembers members, IResourceServiceClient resources, CancellationToken cancellationToken) =>
{
    if (!await members.IsMemberAsync(projectId, user, cancellationToken)) return Results.NotFound();
    return Results.Resource(resources.GetResourceContainerClient($"project-{projectId}"), name);
}).RequireAuthorization();
```

`IProjectMembers` stands for however your application records who belongs where. Answering 404 rather than 403 for
a project someone isn't in avoids telling them it exists. ASP.NET Core's
[resource-based authorization](https://learn.microsoft.com/aspnet/core/security/authorization/resourcebased) is
another way to write the same check.

## 3. Choose file names yourself

Within a container, a name is just a key: `report.pdf` and `../report.pdf` are different names, and nothing on disk
is involved, so names can't escape their container. But a user who picks the name can **overwrite another file** in
the same container, or fill it with names your application doesn't expect.

- **Generate the name** for uploads, for example from a new ID, and keep the user's file name as metadata if you want
  to show it. [Accepting uploads](accepting-uploads.md#from-an-html-form) shows this for forms.
- **Or check the name** against what the user may write: a prefix of their own (`drafts/{userId}/`), or a fixed set
  of names.
- **Use [`CreateOnly`](../uploading.md)** when an upload should never replace an existing file.

## 4. Limit what can be uploaded

- **Sizes.** Web servers refuse request bodies over about 30 MB by default; raise that only for the endpoints that
  need it ([large uploads](accepting-uploads.md#large-uploads)). Set
  [`MaxResourceSize` and `MaxContainerSize`](../container-policies.md) so no single user can fill your database.
- **File types.** [`AllowedContentTypes`](../container-policies.md) checks the type the uploader *declares*, not
  what the file really is: someone can upload a web page and call it `image/png`. Treat the declared type as a hint,
  list exact types rather than `image/*` (which includes SVG images, which can run scripts), and if it matters, check
  the file's first bytes or scan it before trusting it.
- **Showing files in the browser.** Downloads are offered as files to save, not shown, unless you set `Inline`. Even
  with `Inline`, only types that are safe to show are displayed: common image formats, PDF, audio, video and plain
  text <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>. A web page or SVG image shown as part of
  your site could run scripts as if it were one of your pages, which is why the rest are downloaded.
- **Forms.** An upload form is a target for other sites: a page elsewhere can make a signed-in user's browser submit
  it. Keep ASP.NET Core's antiforgery protection on, as [Accepting uploads](accepting-uploads.md#from-an-html-form)
  shows.

## 5. Keep links and names out of logs

Logs and traces are read by more people than your files are, and kept for a long time.

- **Signed links are passwords.** Anyone with a link can open the file until it expires, and the link sits in the URL
  of every request that opens it. ASP.NET Core logs request URLs at the `Information` level, and OpenTelemetry records
  them on each request's span as `url.path`. Hide the token from both: raise the log level of
  `Microsoft.AspNetCore.Hosting.Diagnostics` to `Warning` in production, and replace the path on the span:

  ```csharp
  builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(options =>
      options.EnrichWithHttpRequest = (activity, request) =>
      {
          if (request.Path.StartsWithSegments("/shared")) activity.SetTag("url.path", "/shared/{token}");
      }));
  ```

- **File names can be personal.** Names like `contracts/jane-smith.pdf` say something about a person. Resourcefull
  leaves names off its own spans unless you set `RecordResourceNames` (see [Telemetry](../telemetry.md)), but request
  logs and traces include the URL, which usually contains the name. Apply the same care to those as to the token.

## 6. Answer errors without leaking details

Register [`AddResourcefullExceptionHandler`](status-codes.md), so a missing container is a 404 and a broken rule is a
400, 409 or 413, rather than a 500 with a stack trace. Its messages name the container or resource involved, never
your infrastructure, so they're safe to return to the person who asked.

## Checklist

- [ ] Every file endpoint has `RequireAuthorization()`, except the one serving shared links.
- [ ] The container comes from the signed-in user or a membership check, never straight from the URL.
- [ ] Upload names are generated or checked; uploads that mustn't replace a file use `CreateOnly`.
- [ ] Request size limits are raised only where needed, and containers have size limits.
- [ ] Allowed file types are exact types, and nothing relies on the declared type alone.
- [ ] Forms keep antiforgery protection.
- [ ] Shared-link tokens don't reach your logs or traces.
- [ ] The exception handler is registered.
