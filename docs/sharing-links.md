# Sharing files with links

Sometimes you need to give someone a file who can't sign in to your application: a customer downloading an invoice
from an email, a partner fetching a report, a colleague you send a link to in chat. A **signed link** is a URL that lets
whoever has it download one file, without signing in, until a time you choose. After that, the link stops working.

```
https://example.com/shared/CfDJ8Nq0l2xR...a long string of letters...
```

This page explains how signed links work, how to add them to an ASP.NET Core application, and what to think about when
you use them. You don't need any background in security to follow it.

## How it works

A signed link works like a permission slip that only your application can write:

1. **Someone who's allowed to share a file asks for a link.** Your application, at an endpoint that only signed-in users
   can reach, asks Resourcefull for a **token**: a long string that says "this file, until this time".
2. **Resourcefull seals the token.** It's signed and encrypted with your application's own secret keys, using a part of
   ASP.NET Core called [Data Protection](#keeping-links-working-data-protection-keys). Signing means that if anyone
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
one instance of your application, or restart it, read [Keeping links working](#keeping-links-working-data-protection-keys)
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
| works | the file, with its name and type. Big files can be resumed and videos can seek, as with [range requests](recipes/aspnet-core.md#range-requests). |
| has expired | **410 Gone**: the link existed but has run out. |
| was changed, made up, or sealed with keys this application no longer has | **404 Not Found**: nothing says whether such a file exists. |
| works, but the file (or the version it shows) has been deleted | **404 Not Found** |

Every response, the errors included, tells browsers not to pass the link on to other websites
(`Referrer-Policy: no-referrer`) and tells shared caches not to keep the file (`Cache-Control: private`).

## Stopping a link from working

A link doesn't live anywhere: it's checked each time it's opened, so there's no list of links to delete one from. To
stop people opening a file through links you've already given out:

- **Keep expiry times short.** A link that expires in an hour can only leak for an hour. This is the main protection,
  so pick the shortest time that works for the people you share with.
- **Delete or move the file.** Links to it stop working. A link that shows one version also stops when that version is
  deleted, so restoring an older version or uploading a new one doesn't revive it.
- **Throw away the keys, as a last resort.** Revoking your application's Data Protection keys makes every link ever
  created stop working at once. The same keys usually protect your sign-in cookies too, so this also signs everyone
  out. See Microsoft's guide to [key management](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-management), which covers revoking keys.

If you need to cancel individual links, keep a list of the links you've created in your own database, and check it in
the endpoint that serves them before calling `Results.SharedResource`. `IResourceLinkSigner.Check(token)` tells you
which file and version a token is for, so you can look it up.

## Keeping links working: Data Protection keys

Data Protection is the part of ASP.NET Core that seals tokens, and it's what keeps sign-in cookies safe too. It uses a
set of secret keys, the **key ring**, which it creates and replaces by itself. You need to tell it where to keep them.

Out of the box, ASP.NET Core keeps the keys on the machine it runs on. That's fine on a single server that keeps its
disk, but links stop working when:

- **you run more than one instance,** such as behind a load balancer. A link created on one instance is opened on
  another, which has different keys, so it answers 404.
- **the keys are lost,** such as when a container restarts with a fresh disk. Every link answers 404.

The fix is to store the keys somewhere every instance can read, and to give every instance the same application name:

```csharp
// In a folder every instance can reach, such as a network share or a mounted volume:
builder.Services.AddDataProtection()
    .SetApplicationName("my-application")
    .PersistKeysToFileSystem(new DirectoryInfo(@"\\files\keys\my-application"));

// Or in your application's database, with the Microsoft.AspNetCore.DataProtection.EntityFrameworkCore package:
builder.Services.AddDataProtection()
    .SetApplicationName("my-application")
    .PersistKeysToDbContext<MyDbContext>();
```

Call this before or after `AddResourcefullLinks`; either works. Microsoft's guide to
[configuring Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview)
covers more places to store keys, and how to protect them at rest.

Data Protection replaces its key every 90 days, but keeps the old ones to read what they sealed, so links outlive a
key change; you don't need to do anything when that happens.

## Using it safely

- **Treat a link like a key.** Anyone who has it can open the file until it expires. Links end up in email, chat logs,
  browser history and server logs, so expect them to be seen by more people than you sent them to.
- **Choose the shortest expiry that works.** An hour for something sent in a chat, a few days for an email, longer
  only when you must.
- **Check permission when creating a link, not when opening it.** The endpoint that creates links is where you decide
  who may share what. The endpoint that serves them can't know who's opening it.
- **Serve links only over HTTPS,** so they can't be read on the way.
- **Keep the keys safe.** Whoever has your Data Protection keys can create links to any file, so store them where only
  your application can read them.

## Checking a token yourself

`IResourceLinkSigner.Check(token)` tells you whether a token is valid, expired or invalid, and for a valid or expired
one, which file, version and expiry it carries. Use it to record which links are opened, to keep your own list of
cancelled links, or to serve links some other way:

```csharp
ResourceLinkCheck check = links.Check(token);
if (check.Status == ResourceLinkStatus.Valid)
    logger.LogInformation("Link to {Name} opened", check.Link!.ResourceName);
```

## Telemetry

Each request that opens a link gets `resourcefull.link` on its span: `valid`, `expired` or `invalid`. A valid link's
download records the usual `download` operation. See [Telemetry](telemetry.md).

## Troubleshooting

**Links work for a while, then all answer 404.** The Data Protection keys were lost, most often when the application
restarted somewhere with a fresh disk. Store the keys as in [Keeping links working](#keeping-links-working-data-protection-keys).

**Links work on one server but not another.** The instances have different keys or application names. Give them the
same storage and the same `SetApplicationName`.

**Links to a file answer 404 after it was uploaded again.** The links show the version that was current when they
were made, and the new upload replaced it. See [Which version a link shows](#which-version-a-link-shows): create links
to files you overwrite with `FollowCurrent = true`.

**A link answers 404 straight after it was created.** Check that the URL holds the whole token; some email and chat
programs cut long URLs, or break them across lines.

**`Results.SharedResource` throws "needs signed links added with AddResourcefullLinks".** Add
`builder.Services.AddResourcefullLinks()`.
