# Resourcefull — Documentation (v2)

## Prerequisites

- **.NET 10.0+**

> **Upgrading from v1?** v2 is a new major version with a redesigned public API and a database schema
> change. See [Migrating from v1](#migrating-from-v1) and [Upgrading the database schema](#upgrading-the-database-schema)
> before you upgrade. The old `ErrorOr`-based methods were deprecated in v2.0 and **removed in v2.1** —
> if you are coming straight from v1, plan to migrate every call site in a single pass.

## Overview

The Resourcefull code library provides a simple and efficient way to manage resources in your .NET
applications. It supports multiple storage options, including in-memory storage and SQL Server
storage. Resources are organized into **containers**, which help distinguish resources with the same
name. The library includes **versioning** capabilities, allowing you to track and manage multiple
versions of resources.

The API is modeled on the Azure Blob Storage SDK (`BlobServiceClient` / `BlobContainerClient`), so it
should feel familiar and make a future move to blob storage natural.

## Names

Container and resource names follow the same rules on every provider:

- **Case-sensitive.** `Report.pdf` and `report.pdf` are different resources, and `Docs` and `docs` are
  different containers, regardless of the SQL Server's default collation.
- **Length:** a container name is 1–128 characters; a resource name is 1–256 characters.
- **No leading or trailing whitespace.** SQL Server ignores trailing spaces when comparing strings, so
  names that differ only there are rejected rather than silently treated as the same name.

Every method that takes a name checks it first and throws `ArgumentException` (`ArgumentNullException`
for null) before touching storage.

## Setup

Install the NuGet packages you need:

```bash
dotnet add package Resourcefull
dotnet add package Resourcefull.InMemory   # in-memory provider (tests/dev)
dotnet add package Resourcefull.SqlServer  # SQL Server provider
```

Configure Resourcefull in `Program.cs` with `AddResourcefull`:

```csharp
builder.Services.AddResourcefull(o =>
{
    // Configure the provider and options here
});
```

## Storage Providers

Resourcefull supports multiple storage providers, but **only one can be configured at a time**.
Attempting to configure more than one results in an `InvalidOperationException`.

### In-Memory Provider

Ideal for testing and local development; uses in-memory data structures to simulate data access. It keeps the
SQL Server provider's behavior where tests could notice the difference: for example, its download streams can't
seek either.

```csharp
builder.Services.AddResourcefull(o =>
{
    o.UseInMemory();
});
```

### SQL Server Provider

Connects to a Microsoft SQL Server database using a connection string.

```csharp
builder.Services.AddResourcefull(o =>
{
    o.UseSqlServer("Your Connection String Here");
});
```

**Provisioning the database.** The connection string passed to `UseSqlServer` must name the database that
holds Resourcefull's tables (`ResourceContainers`, `Resources`, `ResourceChunks` and EF Core's
`__EFMigrationsHistory`). That can be a database of its own or your application's database, as long as no
tables of yours share those names. Provisioning turns on `READ_COMMITTED_SNAPSHOT`, which is a setting of the
whole database: in a shared database it also changes how your application's own queries read (from row versions
rather than waiting on locks, with the version store in `tempdb`). If your application relies on read-committed
blocking, give Resourcefull a database of its own. Either way, provision it in one of two ways:

- **From the application.** Call `MigrateResourcefullDatabaseAsync` on the built service provider. It creates
  the database if it is missing and applies only the migrations it lacks:

  ```csharp
  var app = builder.Build();
  await app.Services.MigrateResourcefullDatabaseAsync();
  ```

  Run it from a deployment step, or from one instance at startup. On a database that doesn't have
  `READ_COMMITTED_SNAPSHOT` yet, enabling it disconnects every other session (see
  [below](#read_committed_snapshot)).
- **With the scripts.** Create the database, then run `Resourcefull.sql` connected to it:

  ```shell
  sqlcmd -S <server> -d <database> -b -I -i Resourcefull.sql
  ```

  The scripts run against whichever database they are connected to (`-d`) and never create or switch
  databases. `-b` stops at the first error, and `-I` turns on `QUOTED_IDENTIFIER`, which the filtered
  indexes need. `Resourcefull.sql` brings any database, empty or older, to the latest schema and is safe to
  run again. To upgrade an existing database step by step, see
  [Upgrading the database schema](#upgrading-the-database-schema).

The scripts ship in the package's `scripts` folder, for example
`~/.nuget/packages/resourcefull.sqlserver/<version>/scripts/` (`Resourcefull.sql`, plus one upgrade script per
migration under `Migrations/`). They are not added to the projects that reference the package. They are
also in the repository under `src/Resourcefull.SqlServer/Scripts/`. They are generated from the package's
EF Core migrations, and the integration test suite fails if they stop matching them. See
[Regenerating the scripts](#regenerating-the-scripts) if you maintain the package.

#### READ_COMMITTED_SNAPSHOT

The SQL Server provider is designed to run with `READ_COMMITTED_SNAPSHOT` (RCSI) **enabled** on the
database that holds its tables. RCSI applies to the whole database, so in a database shared with your application
it applies to your tables too. The provided scripts enable it for you. RCSI lets reads use row-versioning instead
of taking shared locks, which removes a class of reader/writer deadlocks under concurrent load.

The provider also enables a **retry policy** (including SQL Server deadlock victims, error 1205) so
transient contention does not surface to your code.

#### Large resources

Content moves to and from the database a little at a time, so memory use stays roughly constant whatever the
size of a resource:

- **Uploads** stream into the database in one transaction, so a version isn't visible until all of its content
  is stored. The resource name is locked only at the end, to make the new version current, so concurrent uploads
  of one name don't wait for each other's content. A transient failure partway through is retried only when
  the content stream can seek (a `MemoryStream`, a `FileStream`, or the `BinaryData` overload). A non-seekable
  stream, such as a request body, fails instead, and the caller can upload again.
- **Downloads** read the content as the returned stream is read. If the version is deleted while it is being
  read, the stream throws an `IOException` rather than ending early.

### Writing a provider

To store resources somewhere else (a file system, object storage, another database), implement
`IResourceRepository` and register it with `UseProvider`. The interface follows semantic versioning: it changes
incompatibly only in a major release of `Resourcefull`, so a provider package can depend on a version range.

```csharp
public static class ResourcefullFileSystemOptionsExtensions
{
    public static ResourcefullOptionsBuilder UseFileSystem(this ResourcefullOptionsBuilder builder, string root)
    {
        builder.Services.AddSingleton(new FileSystemOptions(root));
        return builder.UseProvider<FileSystemRepository>(ServiceLifetime.Singleton);
    }
}
```

The XML docs on each member state its contract. In short, a provider must:

- **Treat names and version IDs as untrusted.** The clients only guarantee that names follow the
  [name rules](#names) (not blank, within the length limits, no whitespace at either end); `/`, `\`, `..` and any
  other characters can still arrive, and version IDs on reads and deletes aren't checked at all. Encode or escape
  them before using them as file paths or storage keys.
- **Compare names and version IDs ordinally** (case-sensitive), and size its storage for
  `ResourceNames.MaxContainerNameLength`, `MaxResourceNameLength` and `MaxVersionIdLength`. These limits change
  only in a major release.
- **Keep exactly one current version per name.** The current version is the one stored most recently, not the
  one with the latest `LastModified`, and concurrent writes and deletes must never leave two.
- **Decide conflicts in the write itself:** a missing container (`ResourceContainerNotFoundException`), a
  create-only write to an existing name or a duplicate container (`ResourceConflictException`).
- **Measure what it stores:** read the content through a `HashingStream`, store the measured length, and return
  `HashingStream.Complete(declaredLength, lastModified)`, which also rejects a wrong declared length. Call it as
  the write makes the version current but before it commits, so a wrong length stores nothing.
- **Stamp the time a version becomes current:** pass `Complete` the time the write makes the version current, not
  the time before its content was read, and store it as the version's `LastModified`. Ignore the `LastModified`
  the caller passes. Take an optional `TimeProvider` in the constructor, so an app or a test can set the clock.
- **Return content as a read-only, forward-only stream**, like the built-in providers: `CanSeek` and `CanWrite`
  false, `Length` the content's full length, `Position` the bytes read so far, and `NotSupportedException` for
  seeking, setting `Position`, writing and `SetLength`.
- **Store container metadata and URI**, and hand back copies so callers can't change what is stored.
- **Store the content hash** from `WrittenContent` with the version, and return it as `ResourceInfo.ContentHash`
  on every read.
- **Honor cancellation:** a cancelled operation throws `OperationCanceledException` and leaves nothing half-done.
  The one exception is deleting a container, which may stop between whole versions, as long as each remaining name
  keeps its current version and calling it again finishes the job.
- **Be safe to call concurrently** for the lifetime it registers with.

### URI Provider

The `IUriProvider` interface allows custom URI generation for resources, simplifying integration with
custom Web APIs. Implement `GetAbsoluteUri`, which takes a `ResourcefullContext` (container name,
optional resource name, optional version ID) and returns the absolute URI as a string.

```csharp
public class MyUriProvider(IConfiguration configuration) : IUriProvider
{
    private readonly string? _baseUrl = configuration["BaseUrl"];

    public string? GetAbsoluteUri(ResourcefullContext context)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return null;
        var baseUrl = _baseUrl.Trim('/');

        if (!string.IsNullOrEmpty(context.ResourceName))
        {
            var uri = $"{baseUrl}/resources/{context.ContainerName}/?resourceName={context.ResourceName}";
            if (!string.IsNullOrEmpty(context.ResourceVersionId))
                uri += $"&versionId={context.ResourceVersionId}";
            return uri;
        }

        return $"{baseUrl}/containers/{context.ContainerName}";
    }
}
```

```csharp
builder.Services.AddResourcefull(o =>
{
    o.SetUriProvider<MyUriProvider>();
});
```

## Versioning

Resourcefull supports versioning, enabling you to manage multiple versions of resources. When
versioning is enabled, each resource can have multiple versions, each identified by a unique
`VersionId`. If the optional `VersionId` is not provided, the **current** version is used for reads,
and write operations create a new version.

A `VersionId` is an opaque string; compare it for equality and don't parse it. New versions get a GUID
(version 7), and IDs written by earlier releases (timestamps) stay valid. The **store** decides which
version is current and the order versions are listed in, by the order it stored them, so servers with
skewed clocks can't make an older write current. `LastModified` records when the version became current, by
the clock of the server that stored it, and is informational only.

- **Enable versioning:** call `UseVersioning`.
- **MaxVersions:** limits how many versions are kept per resource. Older versions are pruned
  automatically when the limit is exceeded.
- **Deleting:** `DeleteResourceIfExistsAsync(name)` deletes the resource with its whole history; pass a
  `versionId` to delete just that version. Earlier releases deleted only the current version here; to remove
  just the latest upload, pass its `versionId`.
- **`ResourceInfo.IsLatest`:** every returned `ResourceInfo` carries an `IsLatest` flag. When you list
  versions with `GetResourceVersionsAsync`, exactly one entry has `IsLatest = true` — the current
  version — so you no longer need positional tricks to find it.

```csharp
builder.Services.AddResourcefull(o =>
{
    o.UseVersioning(maxVersions: 10);
});
```

## Error handling

> **v2 removes the `ErrorOr` return wrapper.** Methods now return their value directly and follow
> standard .NET conventions, matching the Azure Blob SDK style.

| Outcome | v2 behavior |
|---|---|
| Item not found (single read) | returns `null` |
| Listing with no results | returns an **empty** `IReadOnlyList<T>` (never null) |
| Existence / conditional delete | returns `bool` |
| Invalid argument (null/empty, or a name that breaks the [name rules](#names)) | throws `ArgumentException` / `ArgumentNullException` |
| Name conflict on create/upload | throws `ResourceConflictException` |
| Upload into a container that does not exist | throws `ResourceContainerNotFoundException` |
| Upload whose declared length differs from its content | throws `ResourceContentLengthMismatchException` |
| Restore while versioning disabled | throws `InvalidOperationException` |

`ResourceConflictException` and `ResourceContainerNotFoundException` derive from
**`ResourcefullException`**, the base type for Resourcefull-specific failures — catch the base type to
handle any of them.

```csharp
try
{
    ResourceInfo info = await container.UploadAsync("report.pdf", stream);
}
catch (ResourceConflictException)
{
    // 409-style conflict: a resource with that name already exists and Overwrite is false
}
catch (ResourceContainerNotFoundException)
{
    // 404-style: the container was never created, or was deleted before the write
}
```

An upload never reports success for a container that does not exist. Both providers raise the same
exception, and the decision is made by the write itself rather than by a check beforehand, so a
container deleted moments before the write is still reported. Reads against a missing container return
`null` or an empty list, as the table above describes.

Deleting a container removes every resource in it on both providers, and the metadata dictionaries you
pass in or receive back are copies: changing them afterwards does not change what is stored.

## Cancellation

Every operation that contacts the store has an overload whose last parameter is a `CancellationToken`.
The original overloads remain and behave exactly as before. A cancelled operation throws
`OperationCanceledException`; it never returns `null`, an empty list or a `ResourceInfo` for work it did
not finish.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
ResourceInfo info = await container.UploadAsync("report.pdf", stream, null, cts.Token);
```

- **A cancelled upload stores nothing.** An upload cancelled before its new version is stored throws
  `OperationCanceledException` and changes nothing. Once the version is stored the upload has succeeded:
  the token no longer applies, and pruning older versions (beyond `MaxVersions`, or the overwritten version
  when versioning is off) runs to completion. If pruning fails, the upload still returns the stored version,
  and the older versions stay until a later overwriting or versioned upload of the name prunes them. Until
  then they are ordinary stored versions: they appear in `GetResourceVersionsAsync` and can be read by
  version ID, and deleting the name removes them with the rest of its history.
- **`DownloadStreamingAsync`** takes a token for locating the resource. To cancel the download itself,
  pass a token to the returned stream's `ReadAsync` or `CopyToAsync`; `DownloadContentAsync` does this
  for you.
- The SQL Server provider's retry policy stops retrying once the token is cancelled.

## Clients

### IResourceServiceClient

The entry point for managing **containers**. Inject `IResourceServiceClient`.

#### GetResourceContainerClient

Returns a client for a container **synchronously, without contacting the store**. The container need
not exist yet; operations report absence when they run. This is the cheap, lazy way to get a
container client and is the recommended replacement for the v1 `CreateResourceContainerClientAsync`.
Since nothing is read, the client's `ResourceContainerInfo` carries the name and the URI provider's URI but
no metadata; call `GetResourceContainerInfoAsync` for the stored metadata.

```csharp
IResourceContainerClient container = resourceServiceClient.GetResourceContainerClient("my-container");
ResourceContent? content = await container.DownloadContentAsync("my-resource");
```

#### CreateResourceContainerAsync

Creates a new container. **Throws `ResourceConflictException`** if it already exists, including when a
concurrent call created it first.

- `resourceContainerName: string`
- `metadata: IDictionary<string, string>?` (optional)
- **Returns** `Task<IResourceContainerClient>` — a client for the new container.

```csharp
IResourceContainerClient container = await resourceServiceClient.CreateResourceContainerAsync(
    "my-container",
    metadata: new Dictionary<string, string> { ["team"] = "platform" });
```

#### CreateResourceContainerIfNotExistsAsync

Creates the container if it does not exist; returns a client either way (no exception if it already
exists). The client's `ResourceContainerInfo` is what is stored: for an existing container, its own metadata
and URI, not the ones passed here.

- `resourceContainerName: string`
- `metadata: IDictionary<string, string>?` (optional, applied only on creation)
- **Returns** `Task<IResourceContainerClient>`.

```csharp
IResourceContainerClient container =
    await resourceServiceClient.CreateResourceContainerIfNotExistsAsync("my-container");
```

#### GetResourceContainerInfoAsync

Retrieves a container's attributes.

- `resourceContainerName: string`
- **Returns** `Task<ResourceContainerInfo?>` — `null` if the container does not exist.

```csharp
ResourceContainerInfo? info = await resourceServiceClient.GetResourceContainerInfoAsync("my-container");
if (info is null) { /* not found */ }
```

#### GetResourceContainersAsync

Lists all containers.

- **Returns** `Task<IReadOnlyList<ResourceContainerInfo>>` (possibly empty).

```csharp
IReadOnlyList<ResourceContainerInfo> containers = await resourceServiceClient.GetResourceContainersAsync();
```

#### ContainerExistsAsync

Checks whether a container exists.

- `resourceContainerName: string`
- **Returns** `Task<bool>`.

```csharp
bool exists = await resourceServiceClient.ContainerExistsAsync("my-container");
```

#### DeleteResourceContainerIfExistsAsync

Deletes a container (and everything in it) if it exists. The SQL Server provider deletes a large container in
batches that each remove a bounded amount of content and commit one at a time, taking whole versions and each
name's current version last. If the delete is cancelled or fails partway, the versions it reached are gone, every
other version is still whole and each remaining name still has its current version, and the container still
exists: call it again to finish.

- `resourceContainerName: string`
- **Returns** `Task<bool>` — `true` if deleted, `false` if it did not exist.

```csharp
bool wasDeleted = await resourceServiceClient.DeleteResourceContainerIfExistsAsync("my-container");
```

---

### IResourceContainerClient

Manages **resources within a container**. Obtain one from
`IResourceServiceClient.GetResourceContainerClient(...)` (or a `Create…` method).

> `IResourceContainerClient` implements `IDisposable` for backwards compatibility, but in v2 the
> client is a lightweight handle that shares the service's scope — there is nothing to dispose, and
> you do not need to wrap it in `using`.

#### UploadAsync

Uploads a resource. Two overloads accept a `Stream` or in-memory `BinaryData`.

- `resourceName: string`
- `content: Stream` **or** `BinaryData`. A stream is read from its current position to its end, as `CopyTo`
  would read it, so rewind a stream you have just written before uploading it. The stream is not disposed.
- `uploadOptions: ResourceUploadOptions?` (optional)
- **Returns** `Task<ResourceInfo>` describing what was stored: the new version's ID, the measured
  `ContentLength`, the `ContentHash` and `IsLatest = true`, with a copy of the metadata.
- **Throws** `ArgumentException` (blank name), `ArgumentNullException` (null content),
  `ResourceConflictException` (name exists and the upload is create-only: `CreateOnly` is set, or versioning
  is disabled and `Overwrite` is false), `ResourceContentLengthMismatchException` (the declared
  `Headers.ContentLength` differs from the bytes supplied; nothing is stored).

Uploads of the same name never interleave: each provider stores them one at a time (SQL Server holds a lock on
the name until each commits), so concurrent uploads leave exactly one current version, and a successful prune
never leaves more than `MaxVersions` (see [Cancellation](#cancellation) for a prune that fails).
The create-only check is part of the write, so of several concurrent create-only uploads of one name exactly one
succeeds and the rest throw `ResourceConflictException`.

```csharp
var options = new ResourceUploadOptions
{
    Overwrite = true,
    Headers = new(file.ContentType, file.Length),
    Metadata = new Dictionary<string, string> { ["OriginalFileName"] = file.FileName }
};

IResourceContainerClient container = resourceServiceClient.GetResourceContainerClient("my-container");

try
{
    ResourceInfo info = await container.UploadAsync(file.FileName, file.OpenReadStream(), options);
}
catch (ResourceConflictException)
{
    // resource already exists and overwrite was not requested
}
```

#### RestoreVersionAsync

Restores a previous version by creating a new current version from it.

- `resourceName: string`
- `versionId: string`
- **Returns** `Task<ResourceInfo?>` — the new version, or `null` if the resource/version does not exist.
- **Throws** `InvalidOperationException` if versioning is not enabled; `ArgumentException` for blank
  arguments.

```csharp
ResourceInfo? restored = await container.RestoreVersionAsync("my-resource", "2025-05-23T16:02:04.1668897Z");
if (restored is null) { /* version not found */ }
```

#### DownloadStreamingAsync

Downloads a resource as a stream. Prefer this for large resources to avoid buffering the whole payload.

- `resourceName: string`
- `versionId: string?` (optional; current version when null)
- **Returns** `Task<ResourceDownloadResult?>` — `null` if not found. When non-null, `Stream` is
  guaranteed to be present. The stream is read-only and forward-only: `Length` and `Position` report the
  content's length and the bytes read so far, but it can't seek, so copy it into a `MemoryStream` first if you
  need to rewind.

```csharp
ResourceDownloadResult? result = await container.DownloadStreamingAsync("my-resource");
if (result is not null)
{
    Stream stream = result.Stream;
    ResourceInfo info = result.ResourceInfo;
}
```

#### DownloadContentAsync

Downloads a resource's content into memory.

- `resourceName: string`
- `versionId: string?` (optional)
- **Returns** `Task<ResourceContent?>` — `null` if not found.

```csharp
ResourceContent? content = await container.DownloadContentAsync("my-resource");
if (content is not null)
{
    byte[] bytes = content.Content.ToArray();
}
```

#### GetResourceInfoAsync

Retrieves a resource's attributes without downloading content.

- `resourceName: string`
- `versionId: string?` (optional)
- **Returns** `Task<ResourceInfo?>` — `null` if not found.

```csharp
ResourceInfo? info = await container.GetResourceInfoAsync("my-resource");
```

#### GetResourcesAsync

Lists the current version of every resource in the container.

- **Returns** `Task<IReadOnlyList<ResourceInfo>>` (possibly empty).

```csharp
IReadOnlyList<ResourceInfo> resources = await container.GetResourcesAsync();
```

#### GetResourceVersionsAsync

Lists every stored version of a resource: the current version first (`IsLatest = true`), then the rest
from the most recently stored to the oldest.

- `resourceName: string`
- **Returns** `Task<IReadOnlyList<ResourceInfo>>` (possibly empty).

```csharp
IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("my-resource");
IEnumerable<ResourceInfo> history = versions.Where(v => !v.IsLatest); // older versions only
```

#### DeleteResourceIfExistsAsync

Deletes a resource and all its versions, or one version of it, if it exists.

- `resourceName: string`
- `versionId: string?` (optional) — without it, the resource and every version of it are deleted. With it,
  only that version is deleted; if it was the current version, the most recently stored remaining version
  becomes current.
- **Returns** `Task<bool>` — `true` if anything was deleted.

```csharp
bool wasDeleted = await container.DeleteResourceIfExistsAsync("my-resource");            // all versions
bool versionDeleted = await container.DeleteResourceIfExistsAsync("my-resource", versionId); // one version
```

#### ExistsAsync

Checks whether a resource (or specific version) exists.

- `resourceName: string`
- `versionId: string?` (optional)
- **Returns** `Task<bool>`.

```csharp
bool exists = await container.ExistsAsync("my-resource");
```

## Models

### ResourceUploadOptions

Configuration for an upload.

- `Overwrite: bool` — overwrite an existing resource. Only used when versioning is **disabled**
  (versioning creates a new version per write, so there is nothing to overwrite).
- `CreateOnly: bool` — store the upload only if the name doesn't exist yet, with or without versioning;
  otherwise throw `ResourceConflictException`. Takes precedence over `Overwrite`.
- `Headers: ResourceHeaders` — `ContentType` and `ContentLength`.
- `Metadata: IDictionary<string, string>` — additional key/value metadata.

### ResourceContainerInfo

- `Name: string`
- `Uri: string?`
- `Metadata: IDictionary<string, string>`

### ResourceInfo

- `ContainerName: string`
- `Name: string`
- `VersionId: string?` — opaque; see [Versioning](#versioning).
- `LastModified: DateTimeOffset` — when the version became current (the moment its write committed, not when the
  upload began), by the storing server's clock; informational. A registered `TimeProvider` is used as that clock.
- `IsLatest: bool` — **new in v2.** `true` when this is the current version. In a versions list,
  exactly one entry has this set.
- `ContentType: string?`
- `ContentLength: long?` — measured from the bytes stored. Versions stored by earlier releases keep the
  length their caller declared.
- `ContentHash: string?` — SHA-256 of the content as `sha256:` plus lowercase hex, computed while it was
  written and stored with the version. `null` means not recorded yet, never a mismatch: versions stored before
  hashes were kept have none until the [content hash backfill](#recording-hashes-for-older-versions) reaches them.
- `Uri: string?`
- `Metadata: IDictionary<string, string>`

### ResourceHeaders

- `ContentType: string?`
- `ContentLength: long?` — optional. When given, it must match the bytes supplied, or the upload throws
  `ResourceContentLengthMismatchException` and stores nothing.

### ResourceDownloadResult

Returned by `DownloadStreamingAsync`.

- `ResourceInfo: ResourceInfo`
- `Stream: Stream` — the content stream; the caller disposes it.

### ResourceContent

**New in v2.** Returned by `DownloadContentAsync`.

- `ResourceInfo: ResourceInfo`
- `Content: BinaryData` — the resource's content buffered in memory.

### ResourcefullContext

A record representing the context of a Resourcefull operation (passed to `IUriProvider`).

- `ContainerName: string`
- `ResourceName: string?`
- `ResourceVersionId: string?`

## Exceptions

**New in v2.**

- `ResourcefullException` — base type for Resourcefull-specific failures.
- `ResourceConflictException : ResourcefullException` — an operation conflicts with an existing
  resource or container (e.g. uploading a name that already exists with overwrite off, or creating a
  container that already exists). Maps naturally to an HTTP 409.
- `ResourceContentLengthMismatchException : ResourcefullException` — an upload declared a content length
  that differs from the bytes it supplied; nothing was stored. Maps naturally to an HTTP 400.

## Migrating from v1

v2 replaces the `ErrorOr` return wrapper with direct return values and exceptions, and renames several
methods to match the Azure Blob SDK. The v1 methods were marked `[Obsolete]` in v2.0 and have been
**removed in v2.1**, so a v1 → v2.1 upgrade must move every call site off the old API in one pass. A
step-by-step upgrade guide for consuming applications is available in `docs-v2.md`.

> **Uploads no longer rewind the stream.** Earlier releases moved a seekable stream back to position 0 before
> uploading it. Uploads now read from the stream's current position, as `Stream.CopyTo` and the Azure SDK do,
> so a `MemoryStream` you have just written must be rewound (`stream.Position = 0`) before you upload it, or
> the upload stores nothing.

### Service client (`IResourceServiceClient`)

| v1 (deprecated) | v2 replacement |
|---|---|
| `CreateResourceContainerClientAsync(name)` (async, throws if missing) | `GetResourceContainerClient(name)` (sync, lazy) |
| `CreateResourceContainerAsync(ResourceContainerInfo)` → `ErrorOr<…>` | `CreateResourceContainerAsync(name, metadata?)` → throws on conflict |
| — | `CreateResourceContainerIfNotExistsAsync(name, metadata?)` (new) |
| `GetResourceContainerInfoAsync(name)` → `ErrorOr<…>` | `GetResourceContainerInfoAsync(name)` → `…?` (same name, now returns `ResourceContainerInfo?` / `null` if missing) |
| `GetAllResourceContainerInfoAsync()` → `ErrorOr<List<…>>` | `GetResourceContainersAsync()` → `IReadOnlyList<…>` |
| `ResourceContainerExistsAsync(name)` → `ErrorOr<bool>` | `ContainerExistsAsync(name)` → `bool` |
| `DeleteResourceContainerIfExistsAsync(name)` → `bool` | unchanged |

### Container client (`IResourceContainerClient`)

| v1 (deprecated) | v2 replacement |
|---|---|
| `UploadAsync(stream, name, options?)` → `ErrorOr<…>` | `UploadAsync(name, stream, options?)` → throws on conflict; also a `BinaryData` overload |
| `RestoreAsync(name, versionId)` → `ErrorOr<…>` | `RestoreVersionAsync(name, versionId)` → `…?` |
| `DownloadAsync(name, versionId?)` → `ErrorOr<…>` | `DownloadStreamingAsync(name, versionId?)` → `…?`, or `DownloadContentAsync(...)` |
| `GetResourceInfoAsync(name, versionId?)` → `ErrorOr<…>` | `GetResourceInfoAsync(name, versionId?)` → `…?` (same name, now returns `ResourceInfo?` / `null` if missing) |
| `GetAllResourceInfoAsync()` → `ErrorOr<List<…>>` | `GetResourcesAsync()` → `IReadOnlyList<…>` |
| `GetAllResourceVersionsAsync(name)` → `ErrorOr<List<…>>` | `GetResourceVersionsAsync(name)` → `IReadOnlyList<…>` |
| `DeleteIfExistsAsync(name, versionId?)` → `ErrorOr<bool>` | `DeleteResourceIfExistsAsync(name, versionId?)` → `bool` |
| `ResourceExistsAsync(name, versionId?)` → `ErrorOr<bool>` | `ExistsAsync(name, versionId?)` → `bool` |

### Before / after

```csharp
// v1
using var container = await service.CreateResourceContainerClientAsync("my-container");
var result = await container.UploadAsync(stream, "report.pdf", options);
if (result.IsError)
{
    if (result.FirstError.Type == ErrorType.Conflict) { /* ... */ }
}
var info = result.Value;

// v2
var container = service.GetResourceContainerClient("my-container");
try
{
    ResourceInfo info = await container.UploadAsync("report.pdf", stream, options);
}
catch (ResourceConflictException) { /* ... */ }
```

## Upgrading the database schema

v2 changes the SQL Server schema (a surrogate key, an `IsLatest` flag, narrower chunk keys, a
clustered index aligned to the access pattern, and `READ_COMMITTED_SNAPSHOT`). To upgrade an existing
v1 database:

1. **Stop the consuming application** (the upgrade rebuilds keys and enabling RCSI disconnects open
   sessions).
2. **Back up the database.**
3. Run the scripts under `scripts/Migrations/` that your database has not yet recorded, oldest first,
   connected to that database, using a tool that halts on the first error (`sqlcmd -d <database> -b -I`, or
   SSMS in SQLCMD mode with `:on error exit` at the top; SSMS otherwise carries on with the next batch),
   during a **maintenance window**. `MigrateResourcefullDatabaseAsync` applies the same migrations.
   Each script covers one migration, is data-preserving, skips itself when its migration is already in
   `__EFMigrationsHistory`, and is safe to re-run if it is interrupted. A database that predates
   versioning needs `migrate_20250513172319_AddVersioning.sql` before
   `migrate_20260624193539_RedesignKeysAndVersioning.sql`; a v1 database with versioning needs only the
   latter.

`migrate_20261028213132_NameLimitsAndCaseSensitiveNames.sql` applies the [name rules](#names) to an
existing database. It sizes the name columns to their limits and switches them to a case-sensitive
collation. Three things to check before running it:

- **Lookups become case-sensitive.** Callers that relied on the server's case-insensitive default must
  pass names in their stored case.
- **Longer names stop the upgrade.** The script fails, and changes nothing, if a stored container name is
  longer than 128 characters or a resource name longer than 256.
- **Names that break the other rules become unreachable.** Earlier versions accepted empty container names
  and names with leading or trailing whitespace. After the upgrade those rows still appear in listings, but
  every method that takes the name rejects it, so they can no longer be read or deleted through the API.
  Rename or delete them first. This finds the ones padded with spaces (check for tabs and other
  whitespace the same way):

  ```sql
  SELECT [Name] FROM [ResourceContainers] WHERE DATALENGTH([Name]) = 0 OR DATALENGTH([Name]) <> DATALENGTH(TRIM([Name]));
  SELECT [ContainerName], [Name] FROM [Resources] WHERE DATALENGTH([Name]) <> DATALENGTH(TRIM([Name]));
  ```

`migrate_20261028220856_UniqueLatestVersion.sql` makes "one current version per name" a database constraint.
Concurrent uploads in earlier releases could leave a name with two current versions; the script keeps the
most recently stored one current before creating the constraint, so it needs no preparation.

`migrate_20261029003116_EnableReadCommittedSnapshot.sql` turns on `READ_COMMITTED_SNAPSHOT`. Where RCSI is
already on, as on any database an earlier release upgraded successfully, it only records itself; otherwise it disconnects
open sessions, so it needs the maintenance window.

**Recovering from an interrupted upgrade by an earlier release.** The `RedesignKeysAndVersioning` script shipped
before this release could stop after committing the new keys but before recording them, for example when enabling
RCSI failed. Re-running it then fails at `ALTER TABLE [Resources] ADD [Id]`. If the last object it creates, the
foreign key `FK_ResourceChunks_Resources_ResourceId` (in `sys.foreign_keys`), exists but `__EFMigrationsHistory`
has no `20260624193539_RedesignKeysAndVersioning` row, the schema change is complete. Record it, then run the
remaining scripts; the RCSI script turns RCSI on:

```sql
INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260624193539_RedesignKeysAndVersioning', N'10.0.14');
```

`migrate_20261029021712_AddContentHash.sql` adds a nullable `ContentHash` column. It needs no preparation and
changes no existing row. Every version written after it is applied records its hash; versions stored before it
report `ContentHash = null` until the backfill below reaches them.

### Recording hashes for older versions

`ContentHashBackfill`, registered by `UseSqlServer`, hashes every version that has no hash yet. It also reports
every version whose recorded `ContentLength` doesn't match its stored content (a length an earlier release took from
the caller).

```csharp
using IServiceScope scope = app.Services.CreateScope();
ContentHashBackfillResult result = await scope.ServiceProvider.GetRequiredService<ContentHashBackfill>().RunAsync();
foreach (ContentLengthMismatch mismatch in result.Mismatches)
{
    logger.LogWarning("{Container}/{Name} {Version}: recorded {Recorded} bytes, stored {Stored}",
        mismatch.ContainerName, mismatch.Name, mismatch.VersionId, mismatch.RecordedLength, mismatch.StoredLength);
}
```

- **It reads the content of each version without a hash once,** so run it when the load allows. The application
  can keep running meanwhile.
- **It is resumable:** it records each hash as soon as it is computed, so you can cancel it at any point, and a
  later run carries on where it stopped. Once every version has a hash, a run reads no content.
- **Every run reports every mismatch:** a version whose recorded length is wrong still gets its hash, and every
  run reports it until you correct its `ContentLength`. A cancelled run therefore loses nothing.

`ContentHash` stays nullable. Making it required would be a later, separate change, once every database has been
backfilled.

New databases provisioned from `Resourcefull.sql` already include the v2 schema and RCSI — no
upgrade step is required. `Resourcefull.sql` is itself idempotent and can be used in place of the
individual upgrade scripts; it applies whichever migrations the database is missing.

## Regenerating the scripts

For maintainers of this package. Every script under `src/Resourcefull.SqlServer/Scripts/` is a short
hand-written preamble followed by the exact output of `dotnet ef migrations script --idempotent`, and
`ProvisioningScriptTests` in the integration suite regenerates each body from the current model and fails
if the committed file differs. After adding a migration, regenerate all of them from the repository root:

```powershell
# Fresh install: every migration from an empty server
dotnet ef migrations script --idempotent --project src/Resourcefull.SqlServer --output src/Resourcefull.SqlServer/Scripts/Resourcefull.sql

# One upgrade script per migration after the first: <previous migration> <this migration>
dotnet ef migrations script 20240611171637_InitialCreate 20250513172319_AddVersioning --idempotent --project src/Resourcefull.SqlServer --output src/Resourcefull.SqlServer/Scripts/Migrations/migrate_20250513172319_AddVersioning.sql
dotnet ef migrations script 20250513172319_AddVersioning 20260624193539_RedesignKeysAndVersioning --idempotent --project src/Resourcefull.SqlServer --output src/Resourcefull.SqlServer/Scripts/Migrations/migrate_20260624193539_RedesignKeysAndVersioning.sql
dotnet ef migrations script 20260624193539_RedesignKeysAndVersioning 20261028213132_NameLimitsAndCaseSensitiveNames --idempotent --project src/Resourcefull.SqlServer --output src/Resourcefull.SqlServer/Scripts/Migrations/migrate_20261028213132_NameLimitsAndCaseSensitiveNames.sql
dotnet ef migrations script 20261028213132_NameLimitsAndCaseSensitiveNames 20261028220856_UniqueLatestVersion --idempotent --project src/Resourcefull.SqlServer --output src/Resourcefull.SqlServer/Scripts/Migrations/migrate_20261028220856_UniqueLatestVersion.sql
dotnet ef migrations script 20261028220856_UniqueLatestVersion 20261029003116_EnableReadCommittedSnapshot --idempotent --project src/Resourcefull.SqlServer --output src/Resourcefull.SqlServer/Scripts/Migrations/migrate_20261029003116_EnableReadCommittedSnapshot.sql
dotnet ef migrations script 20261029003116_EnableReadCommittedSnapshot 20261029021712_AddContentHash --idempotent --project src/Resourcefull.SqlServer --output src/Resourcefull.SqlServer/Scripts/Migrations/migrate_20261029021712_AddContentHash.sql
```

Then put the preamble back at the top of each file. It is the same for every script, and only turns on
`XACT_ABORT`; the scripts never name a database. The generated scripts run each transactional migration as one
`BEGIN TRANSACTION … COMMIT` batch, and some errors (a duplicate key or a constraint violation in an `INSERT`
or `UPDATE`) stop only the failing statement, so without it the rest of the migration could still commit,
along with its history row:

```sql
SET XACT_ABORT ON;
GO

```

Every `.sql` file under `Scripts/` is packed into the package's `scripts` folder, so a new upgrade script needs
no project change. The test that fails names the script and prints the command that regenerates it.

Three rules keep the generated part generated, and the scripts safe to re-run:

- **Data fixes go in the migration**, as `migrationBuilder.Sql(...)`, never typed into a script by hand.
  The parity test compares text, so a hand-added statement fails it, and a regeneration would drop it.
- **Wrap raw SQL that reads a column added in the same migration in `EXEC(N'...')`.** A generated script
  runs each migration as one batch, and SQL Server binds column names when it compiles the batch, before
  the `ALTER TABLE` earlier in the same batch has run. The backfills in `AddVersioning` and
  `RedesignKeysAndVersioning` are written this way.
- **Give a step that can't run in a transaction a migration of its own.** A `suppressTransaction: true` step
  (such as `ALTER DATABASE`) also moves the migration's history row out of its transaction, so a failure
  between the two leaves the changes in place with no record of them. Make the step check whether its work is
  already done, as `EnableReadCommittedSnapshot` does. The integration suite stops each script after every
  batch and runs it again, and fails if any of those runs can't finish.

Expect the whole file to change cosmetically when the EF Core tooling version changes; the parity test
compares against the generator the package references, so regenerate and commit the result.
