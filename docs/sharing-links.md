# Sharing files with links

Sometimes you need to give someone a file who can't sign in to your application: a customer downloading an invoice
from an email, a partner fetching a report, a colleague you send a link to in chat. A **signed link** is a URL that lets
whoever has it download one file, without signing in, until a time you choose. After that, the link stops working.

```
https://example.com/shared/CfDJ8Nq0l2xR...a long string of letters...
```

This page explains how signed links work, how to add them to an ASP.NET Core application, and what people see when
they open one. [Shared links in production](sharing-links-in-production.md) covers keeping them working across servers
and restarts, and using them safely. You don't need any background in security to follow either page.

## How it works

A signed link works like a permission slip that only your application can write:

1. **Someone who's allowed to share a file asks for a link.** Your application, at an endpoint that only signed-in users
   can reach, asks Resourcefull for a **token**: a long string that says "this file, until this time".
2. **Resourcefull seals the token.** It's signed and encrypted with your application's own secret keys, using a part of
   ASP.NET Core called [Data Protection](sharing-links-in-production.md#keeping-links-working-data-protection-keys). Signing means that if anyone
   changes even one character, the token no longer counts. Encrypting means nobody can read what's inside, so the
   file's name doesn't show up in the link.
3. **Your application puts the token into a URL** and gives it to the person sharing, who sends it on.
4. **When someone opens the link,** your application hands the token to Resourcefull, which checks that your
   application sealed it and that it hasn't expired, and then sends the file.

The link itself is the permission: whoever has it can open the file until it expires, so share links the way you'd
share a key.

## Setting it up

Signed links are part of the `Resourcefull.AspNetCore` package. There are three steps.

### 1. Register signed links

```csharp
using Resourcefull.AspNetCore;

builder.Services.AddResourcefull(options => options.UseSqlServer(connectionString));
builder.Services.AddResourcefullLinks();
```

`AddResourcefullLinks` also turns on Data Protection if your application doesn't use it already. If you run more than
one instance of your application, or restart it, read [Keeping links working](sharing-links-in-production.md#keeping-links-working-data-protection-keys)
before going live.

### 2. Add an endpoint that creates links

This is your application's endpoint, so you decide who may share what. Here, any signed-in user can share any file in
a container; a real application would check that the user may see that file:

```csharp
app.MapPost("/share/{container}/{**name}", async (string container, string name,
    IResourceLinkSigner links, CancellationToken cancellationToken) =>
{
    string? token = await links.CreateTokenAsync(new ResourceLink(container, name)
    {
        ExpiresIn = TimeSpan.FromDays(7)
    }, cancellationToken);

    return token is null
        ? Results.NotFound()
        : Results.Ok(new { url = $"https://example.com/shared/{token}" });
}).RequireAuthorization();
```

`CreateTokenAsync` returns `null` when the file doesn't exist, so you can tell the user rather than hand out a link that
won't work.

### 3. Add the endpoint that serves links

This endpoint must **not** require sign-in, because the people opening links can't sign in:

```csharp
app.MapMethods("/shared/{token}", ["GET", "HEAD"], (string token, IResourceServiceClient resources) =>
    Results.SharedResource(resources, token));
```

The token is safe to put in a URL path: it only uses letters, digits, `-` and `_`.

## What a link can do

`ResourceLink` describes the link you're creating:

| Property | Default | What it does |
|---|---|---|
| `ExpiresIn` | one hour | How long the link works, from when it's created. Anything from seconds to years. |
| `VersionId` | the current version | Which version the link shows. See [Which version a link shows](#which-version-a-link-shows). |
| `FollowCurrent` | off | Show whichever version is current each time the link is opened, instead of one version. |
| `Inline` | off | Ask browsers to display the file, such as a PDF or an image, rather than download it. |

Only set `Inline` for file types you're happy for browsers to display as part of your site. A file uploaded as a web
page (`text/html`) or an SVG image and shown inline can run scripts as if it were one of your pages.

### Which version a link shows

With [versioning](versioning.md), a file can have several versions. By default, a link shows the version that was
current **when the link was created**. If someone uploads a newer version later, people opening the old link still get
what was shared with them, which is usually what you want: a link to "the signed contract" shouldn't quietly turn into
a link to a later draft.

To share whatever the file holds now and later, set `FollowCurrent = true`. Think of that link as a standing
permission to read the name until it expires.

**If you overwrite files, use `FollowCurrent`.** Every upload stores a new version, even without versioning turned on.
Without versioning, uploading a file again replaces its only version, so a link that shows the old version answers
404 from then on. With versioning, the same happens once the old version is pruned past `MaxVersions`. For a file you
keep updating in place, such as a price list or a dashboard export, create its links with `FollowCurrent = true`, so
they always show the latest upload.

## What people see when they open a link

| The link | They get |
|---|---|
| works | the file, with its name and type. Big files can be resumed and videos can seek, as with [range requests](aspnet-core/serving-downloads.md#range-requests). |
| has expired | **410 Gone**: the link existed but has run out. |
| was changed, made up, or sealed with keys this application no longer has | **404 Not Found**: nothing says whether such a file exists. |
| works, but the file (or the version it shows) has been deleted | **404 Not Found** |

Every response, the errors included, tells browsers not to pass the link on to other websites
(`Referrer-Policy: no-referrer`) and tells shared caches not to keep the file (`Cache-Control: private`).

## Telemetry

Each request that opens a link gets `resourcefull.link` on its span: `valid`, `expired` or `invalid`. A valid link's
download records the usual `download` operation. See [Telemetry](telemetry.md).

## Troubleshooting

**Links work for a while, then all answer 404.** The Data Protection keys were lost, most often when the application
restarted somewhere with a fresh disk. Store the keys as in [Keeping links working](sharing-links-in-production.md#keeping-links-working-data-protection-keys).

**Links work on one server but not another.** The instances have different keys or application names. Give them the
same storage and the same `SetApplicationName`.

**Links to a file answer 404 after it was uploaded again.** The links show the version that was current when they
were made, and the new upload replaced it. See [Which version a link shows](#which-version-a-link-shows): create links
to files you overwrite with `FollowCurrent = true`.

**A link answers 404 straight after it was created.** Check that the URL holds the whole token; some email and chat
programs cut long URLs, or break them across lines.

**`Results.SharedResource` throws "needs signed links added with AddResourcefullLinks".** Add
`builder.Services.AddResourcefullLinks()`.
