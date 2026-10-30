# Shared links in production

Before you rely on [signed links](sharing-links.md), make sure they keep working when you run more than one
server or restart one, and decide how you'll stop links you no longer want to work. This page covers both, and the
habits that keep shared files safe.

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

## Checking a token yourself

`IResourceLinkSigner.Check(token)` tells you whether a token is valid, expired or invalid, and for a valid or expired
one, which file, version and expiry it carries. Use it to record which links are opened, to keep your own list of
cancelled links, or to serve links some other way:

```csharp
ResourceLinkCheck check = links.Check(token);
if (check.Status == ResourceLinkStatus.Valid)
    logger.LogInformation("Link to {Name} opened", check.Link!.ResourceName);
```
