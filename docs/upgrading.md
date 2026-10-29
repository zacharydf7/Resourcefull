# Upgrading to 3.0

3.0 is the first release of Resourcefull meant for general use. If you used an earlier release, this page lists what
changed and how to move your code and your database across.

## Upgrading your code

### Results instead of `ErrorOr`

Methods return their value directly and report failures with exceptions, following .NET conventions. A missing item is
`null`, an empty listing is an empty list, and conflicts and missing containers throw (see [Errors](errors.md)).

```csharp
// Before
using var container = await service.CreateResourceContainerClientAsync("reports");
var result = await container.UploadAsync(stream, "report.pdf", options);
if (result.IsError && result.FirstError.Type == ErrorType.Conflict) { /* ... */ }

// 3.0
IResourceContainerClient container = service.GetResourceContainerClient("reports");
try
{
    ResourceInfo info = await container.UploadAsync("report.pdf", stream, options);
}
catch (ResourceConflictException) { /* ... */ }
```

### Renamed methods

| Before | 3.0 |
|---|---|
| `CreateResourceContainerClientAsync(name)` | `GetResourceContainerClient(name)`: synchronous, and doesn't contact storage |
| `CreateResourceContainerAsync(ResourceContainerInfo)` | `CreateResourceContainerAsync(name, metadata?)` |
| | `CreateResourceContainerIfNotExistsAsync(name, metadata?)` (new) |
| `GetAllResourceContainerInfoAsync()` | `GetResourceContainersAsync()` |
| `ResourceContainerExistsAsync(name)` | `ContainerExistsAsync(name)` |
| `UploadAsync(stream, name, options?)` | `UploadAsync(name, stream, options?)`, plus a `BinaryData` overload |
| `RestoreAsync(name, versionId)` | `RestoreVersionAsync(name, versionId)` |
| `DownloadAsync(name, versionId?)` | `DownloadStreamingAsync(name, versionId?)`, or `DownloadContentAsync` for bytes in memory |
| `GetAllResourceInfoAsync()` | `GetResourcesAsync()` |
| `GetAllResourceVersionsAsync(name)` | `GetResourceVersionsAsync(name)` |
| `DeleteIfExistsAsync(name, versionId?)` | `DeleteResourceIfExistsAsync(name, versionId?)` |
| `ResourceExistsAsync(name, versionId?)` | `ExistsAsync(name, versionId?)` |

Every method that contacts storage also has an overload that takes a `CancellationToken`.

### Behavior changes

Check these even where your code still compiles:

- **Uploads read from the stream's current position.** Earlier releases rewound a seekable stream to the start before
  uploading. Rewind a stream you've just written (`stream.Position = 0`), or the upload stores an empty version, which
  with `Overwrite` replaces the existing content.
- **Deleting a name deletes all its versions.** `DeleteResourceIfExistsAsync(name)` without a `versionId` used to
  delete only the current version. Pass the version's ID to delete just that one.
- **Names are case-sensitive and have limits:** 1 to 128 characters for a container, 1 to 256 for a resource, and no
  whitespace at either end. See [Names](containers-and-names.md#names).
- **What's returned is what was stored.** `ContentLength` is measured from the content, and an upload whose declared
  length differs throws `ResourceContentLengthMismatchException`. `LastModified` is when the version became current,
  by the storing server's clock. Every new version has a `ContentHash`.
- **One current version per name.** `ResourceInfo.IsLatest` marks it, and `GetResourceVersionsAsync` lists it first.
- **Download streams can't seek**, and `ResourceDownloadResult.Stream` is never `null`.
- **A failed upload from a non-seekable stream isn't retried** once its content has started. See
  [Large files](uploads-and-downloads.md#large-files).
- **A container client needs no disposing.** It still implements `IDisposable`, but disposing it does nothing.
- **A failed prune is logged** as a warning through `ILogger` instead of being silent.

### Custom providers

`IResourceRepository` is public as of 3.0, so you can store resources anywhere. See
[Writing a provider](providers/writing-a-provider.md).

## Upgrading the database

1. **Back up the database.**
2. **Plan a maintenance window** if any migration the database is missing needs one in the table below. Stop the
   application for `RedesignKeysAndVersioning`, which every database from before 3.0 needs. Turning on
   `READ_COMMITTED_SNAPSHOT` (check `is_read_committed_snapshot_on` in `sys.databases`) disconnects every other
   session.
3. **Prepare for case-sensitive names** (below).
4. **Apply the migrations**: `MigrateResourcefullDatabaseAsync`, or the scripts under `scripts/Migrations/` that the
   database is missing, oldest first, or `Resourcefull.sql` for all of them at once. See
   [Upgrading the schema](providers/sql-server.md#upgrading-the-schema).
5. **Record hashes for older content** with the [backfill](providers/sql-server.md#recording-hashes-for-older-versions),
   whenever the load allows.

What each migration does, and what it needs from you:

| Migration | Changes | Before you run it |
|---|---|---|
| `AddVersioning` | adds version IDs to a database from before versioning | nothing |
| `RedesignKeysAndVersioning` | rebuilds the keys and indexes for streaming and concurrency, and adds `IsLatest` | stop the application: it rebuilds the tables |
| `NameLimitsAndCaseSensitiveNames` | sizes the name columns to their limits and makes them case-sensitive | see below |
| `UniqueLatestVersion` | makes one current version per name a database constraint, repairing any name with two | nothing |
| `EnableReadCommittedSnapshot` | turns on `READ_COMMITTED_SNAPSHOT` | a maintenance window, unless it's already on |
| `AddContentHash` | adds the nullable `ContentHash` column | nothing |

### Case-sensitive names

`NameLimitsAndCaseSensitiveNames` applies the name rules to existing data:

- **Lookups become case-sensitive.** Callers that relied on SQL Server's usual case-insensitive comparison must use
  names in their stored case.
- **Names that are too long stop the upgrade.** It fails, and changes nothing, if a container name is longer than 128
  characters or a resource name longer than 256.
- **Names that break the other rules become unreachable.** Earlier releases accepted empty container names and names
  with whitespace at either end. After the upgrade they still appear in listings, but every method that takes the name
  rejects it, so rename or delete them first. These queries find the ones padded with spaces (check for tabs and other
  whitespace the same way):

  ```sql
  SELECT [Name] FROM [ResourceContainers] WHERE DATALENGTH([Name]) = 0 OR DATALENGTH([Name]) <> DATALENGTH(TRIM([Name]));
  SELECT [ContainerName], [Name] FROM [Resources] WHERE DATALENGTH([Name]) <> DATALENGTH(TRIM([Name]));
  ```
