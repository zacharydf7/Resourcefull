# Introduction

Resourcefull is a .NET library for storing files in your own infrastructure and serving them from your application.
Your code uploads, downloads, lists, versions and deletes files through one client API, and a **storage provider**
decides where the bytes go: SQL Server in production, process memory in tests, or a store of your own.

It's for applications that keep files people upload or download, such as invoices, profile pictures, reports, contract
scans or video, and want them next to the rest of their data, rather than in a cloud service.
[When to use Resourcefull](when-to-use.md) compares it with the alternatives.

## The building blocks

A few words come up on every page:

- **A resource** is a file: its content, plus a description that Resourcefull keeps with it, called its
  `ResourceInfo`: its name, content type, length, a SHA-256 hash of its content, metadata you choose, and when it was
  stored.
- **A container** is a named group of resources, such as `invoices` or `user-42`. Every resource lives in one, and two
  resources can have the same name in different containers. See [Containers](containers.md).
- **A version** is one stored copy of a resource's content. Uploading again stores a new version, and the newest is
  the **current** one. With [versioning](versioning.md) turned on, older versions are kept so you can go back to them.
- **The service client**, `IResourceServiceClient`, is what your code injects. It creates and deletes containers, and
  hands out a **container client**, `IResourceContainerClient`, for working with the resources in one container.
- **A provider** stores the bytes. You choose one when you register Resourcefull, and your code doesn't change if you
  switch.

```csharp
IResourceContainerClient invoices = await resources.CreateResourceContainerIfNotExistsAsync("invoices");
ResourceInfo stored = await invoices.UploadAsync("2026/10/invoice-1042.pdf", pdf);
ResourceDownloadResult? download = await invoices.DownloadStreamingAsync("2026/10/invoice-1042.pdf");
```

## The packages

| Package | What it's for |
|---|---|
| `Resourcefull` | The client API every application uses. |
| `Resourcefull.SqlServer` | Stores files in a SQL Server database. Use it in production. |
| `Resourcefull.InMemory` | Keeps files in process memory, for tests and local development. |
| `Resourcefull.AspNetCore` | Serves and accepts files over HTTP with the right status codes and headers, and creates signed links for sharing them. |

## Where to go next

- **New to Resourcefull?** [Getting started](getting-started.md) installs a package, registers a provider, and stores
  and reads your first file.
- **Working with files:** [uploading](uploading.md), [downloading](downloading.md), [listing](listing.md),
  [copying and moving](copying-and-moving.md), [versioning](versioning.md), and what happens when several callers
  write at once ([concurrency](concurrency.md)).
- **Building a web app:** the [ASP.NET Core](aspnet-core/index.md) section serves downloads, accepts uploads and
  [shares files with links](sharing-links.md).
- **Managing storage:** [container policies](container-policies.md) for quotas and file types,
  [expiring resources](expiry.md), and [backing up and moving containers](backup-and-move.md).
- **Running in production:** setting up [SQL Server](providers/sql-server.md), [health checks](health-checks.md),
  [telemetry](telemetry.md) and [troubleshooting](troubleshooting.md).
- **Testing and extending:** [testing with the in-memory provider](providers/in-memory.md), and
  [writing a provider](providers/writing-a-provider.md) of your own.
- **Looking something up?** The [API reference](../api/index.md) lists every public type and member, and
  [Errors](errors.md) lists what each operation returns or throws.

Contributing to Resourcefull itself? See [CONTRIBUTING.md](../CONTRIBUTING.md).
