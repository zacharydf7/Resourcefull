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

Ideal for testing and local development; uses in-memory data structures to simulate data access.

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

**Provisioning the database:**

- **New database:** execute the bundled `Scripts/Resourcefull.sql` script against your SQL Server
  instance. It creates the `ResourceDB` database if it is missing, brings it to the latest schema and
  enables `READ_COMMITTED_SNAPSHOT` (see [below](#read_committed_snapshot)). The script is idempotent:
  every step is guarded by the migration history, so running it again on a database it already provisioned,
  or on an older database, applies only what is missing.
- **Existing v1 database:** run the upgrade scripts under `Scripts/Migrations/` in order during a
  maintenance window. See [Upgrading the database schema](#upgrading-the-database-schema).

The scripts are generated from the package's EF Core migrations, and the integration test suite fails if
they stop matching them. See [Regenerating the scripts](#regenerating-the-scripts) if you maintain the
package.

#### READ_COMMITTED_SNAPSHOT

The SQL Server provider is designed to run with `READ_COMMITTED_SNAPSHOT` (RCSI) **enabled** on the
resource database. The provided scripts enable it for you. RCSI lets reads use row-versioning instead
of taking shared locks, which removes a class of reader/writer deadlocks under concurrent load.

The provider also enables a **retry policy** (including SQL Server deadlock victims, error 1205) so
transient contention does not surface to your code.

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

- **Enable versioning:** call `UseVersioning`.
- **MaxVersions:** limits how many versions are kept per resource. Older versions are pruned
  automatically when the limit is exceeded.
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
| Invalid argument (null/empty) | throws `ArgumentException` / `ArgumentNullException` |
| Name conflict on create/upload | throws `ResourceConflictException` |
| Upload into a container that does not exist | throws `ResourceContainerNotFoundException` |
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

- **A cancelled upload stores nothing.** When versioning prunes older versions, the new version is written
  first and the prune runs afterwards, so a cancellation between the two leaves one version too many
  (trimmed by the next upload) rather than one too few.
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

```csharp
IResourceContainerClient container = resourceServiceClient.GetResourceContainerClient("my-container");
ResourceContent? content = await container.DownloadContentAsync("my-resource");
```

#### CreateResourceContainerAsync

Creates a new container. **Throws `ResourceConflictException`** if it already exists.

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
exists).

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

Deletes a container (and everything in it) if it exists.

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
- `content: Stream` **or** `BinaryData`
- `uploadOptions: ResourceUploadOptions?` (optional)
- **Returns** `Task<ResourceInfo>`.
- **Throws** `ArgumentException` (blank name), `ArgumentNullException` (null content),
  `ResourceConflictException` (name exists, versioning disabled, `Overwrite` false).

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
  guaranteed to be present.

```csharp
ResourceDownloadResult? result = await container.DownloadStreamingAsync("my-resource");
if (result is not null)
{
    Stream stream = result.Stream!;
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

Lists every stored version of a resource, most recent first. The current version has
`IsLatest = true`.

- `resourceName: string`
- **Returns** `Task<IReadOnlyList<ResourceInfo>>` (possibly empty).

```csharp
IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("my-resource");
IEnumerable<ResourceInfo> history = versions.Where(v => !v.IsLatest); // older versions only
```

#### DeleteResourceIfExistsAsync

Deletes a resource (or a specific version) if it exists.

- `resourceName: string`
- `versionId: string?` (optional)
- **Returns** `Task<bool>` — `true` if a resource was deleted.

```csharp
bool wasDeleted = await container.DeleteResourceIfExistsAsync("my-resource");
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
- `Headers: ResourceHeaders` — `ContentType` and `ContentLength`.
- `Metadata: IDictionary<string, string>` — additional key/value metadata.

### ResourceContainerInfo

- `Name: string`
- `Uri: string?`
- `Metadata: IDictionary<string, string>`

### ResourceInfo

- `ContainerName: string`
- `Name: string`
- `VersionId: string?`
- `LastModified: DateTimeOffset`
- `IsLatest: bool` — **new in v2.** `true` when this is the current version. In a versions list,
  exactly one entry has this set.
- `ContentType: string?`
- `ContentLength: long?`
- `Uri: string?`
- `Metadata: IDictionary<string, string>`

### ResourceHeaders

- `ContentType: string?`
- `ContentLength: long?`

### ResourceDownloadResult

Returned by `DownloadStreamingAsync`.

- `ResourceInfo: ResourceInfo`
- `Stream: Stream?` — the content stream (present when the result itself is non-null).

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

## Migrating from v1

v2 replaces the `ErrorOr` return wrapper with direct return values and exceptions, and renames several
methods to match the Azure Blob SDK. The v1 methods were marked `[Obsolete]` in v2.0 and have been
**removed in v2.1**, so a v1 → v2.1 upgrade must move every call site off the old API in one pass. A
step-by-step upgrade guide for consuming applications is available in `docs-v2.md`.

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
3. Run the scripts under `Scripts/Migrations/` that your database has not yet recorded, oldest first,
   using a tool that halts on the first error (SSMS, or `sqlcmd -b`), during a **maintenance window**.
   Each script covers one migration, is data-preserving, skips itself when its migration is already in
   `__EFMigrationsHistory`, and is safe to re-run if it is interrupted. A database that predates
   versioning needs `migrate_20250513172319_AddVersioning.sql` before
   `migrate_20260624193539_RedesignKeysAndVersioning.sql`; a v1 database with versioning needs only the
   latter.

New databases provisioned from `Scripts/Resourcefull.sql` already include the v2 schema and RCSI — no
upgrade step is required. `Scripts/Resourcefull.sql` is itself idempotent and can be used in place of the
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
```

Then put the preamble back at the top of each file. The fresh-install preamble creates the database when
it is missing and switches to it:

```sql
IF NOT EXISTS (
    SELECT [name] FROM sys.databases WHERE [name] = N'ResourceDB'
)
BEGIN
    CREATE DATABASE [ResourceDB];
END;
GO

USE [ResourceDB];
GO

```

The upgrade preamble only switches to it:

```sql
USE [ResourceDB];
GO

```

Add the new upgrade script to `Resourcefull.SqlServer.csproj` as `Content` like the existing ones so it
ships in the package. The test that fails names the script and prints the command that regenerates it.

Two rules keep the generated part generated:

- **Data fixes go in the migration**, as `migrationBuilder.Sql(...)`, never typed into a script by hand.
  The parity test compares text, so a hand-added statement fails it, and a regeneration would drop it.
- **Wrap raw SQL that reads a column added in the same migration in `EXEC(N'...')`.** A generated script
  runs each migration as one batch, and SQL Server binds column names when it compiles the batch, before
  the `ALTER TABLE` earlier in the same batch has run. The backfills in `AddVersioning` and
  `RedesignKeysAndVersioning` are written this way.

Expect the whole file to change cosmetically when the EF Core tooling version changes; the parity test
compares against the generator the package references, so regenerate and commit the result.
