# Upgrading to 3.0

3.0 is the first release of Resourcefull meant for general use. If you used an earlier release, this page lists what
changed and how to move your code and your database across.

## Upgrading your code

### Results instead of `ErrorOr` (from 1.x)

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

### Renamed methods (from 1.x)

2.x already has these names.

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

Every method that contacts storage also has an overload that takes a `CancellationToken`, as in 2.x.

### Behavior changes

These apply coming from 1.x or 2.x. Check them even where your code still compiles:

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
- **Download streams can't seek**, and `ResourceDownloadResult.Stream` is never `null`. (With SQL Server, 2.x streams
  couldn't seek either; the in-memory provider's could.)
- **A failed upload from a non-seekable stream isn't retried** once its content has started. See
  [Large files](uploads-and-downloads.md#large-files).
- **A container client needs no disposing** (from 1.x). It still implements `IDisposable`, but disposing it does
  nothing.
- **A failed prune is logged** as a warning through `ILogger` instead of being silent.
- **URIs come from your URI provider on every read**, rather than being the one stored when the item was created. If
  your application's base URL has changed, existing containers and versions now report the new URI, and without a URI
  provider `Uri` is `null` even for items stored with one. See [URI providers](uri-providers.md).

### Custom providers

`IResourceRepository` is public as of 3.0, so you can store resources anywhere. See
[Writing a provider](providers/writing-a-provider.md).

## Upgrading the database

1. **Back up the database.**
2. **Plan a maintenance window** if any migration the database is missing needs one in the table below. Stop the
   application for `RedesignKeysAndVersioning`, which databases from 1.x need; databases from 2.x already have it.
   Turning on `READ_COMMITTED_SNAPSHOT` (check `is_read_committed_snapshot_on` in `sys.databases`) disconnects every
   other session; 2.x turned it on already.
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
| `NameLimitsAndCaseSensitiveNames` | sizes the name columns to their limits and makes them case-sensitive, giving each name one spelling | see below; reads and writes of Resourcefull's tables wait while it runs |
| `UniqueLatestVersion` | makes one current version per name a database constraint, repairing any name with two | nothing; reads and writes of Resourcefull's tables wait while it runs |
| `EnableReadCommittedSnapshot` | turns on `READ_COMMITTED_SNAPSHOT` | a maintenance window, unless it's already on |
| `AddContentHash` | adds the nullable `ContentHash` column | nothing |
| `AddCopiedFrom` | adds three nullable columns recording the version a copy or restore came from | nothing |
| `AddExpiresAt` | adds the nullable `ExpiresAt` column, and an index of the versions that expire | nothing |

`NameLimitsAndCaseSensitiveNames` prints two warnings that the maximum key length of
`UX_Resources_Container_Name_Version` is over 900 bytes. They describe the index partway through the migration, before
the name columns shrink to their limits. The finished key is at most 840 bytes, so the warnings can be ignored.

### Case-sensitive names

`NameLimitsAndCaseSensitiveNames` applies the name rules to existing data:

- **Lookups become case-sensitive.** Callers that relied on SQL Server's usual case-insensitive comparison must use
  names in their stored case.
- **Each name keeps one spelling.** Earlier releases treated `Q1.pdf` and `q1.pdf` as one name, so one name's versions,
  or a container's resources, could be stored in several spellings. The upgrade gives every resource its container's
  stored spelling, and every version of a name the spelling of its current version (or, if none is current, of its
  most recently stored one), so each name keeps all its versions. This finds the names that will change:

  ```sql
  SELECT [ContainerName], MIN([Name]) AS [Name], COUNT(DISTINCT CAST([Name] AS varbinary(900))) AS [Spellings]
  FROM [Resources] GROUP BY [ContainerName], [Name] HAVING COUNT(DISTINCT CAST([Name] AS varbinary(900))) > 1;
  ```
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

## Upgrading a running application

The schema can be upgraded before the application. An application on 2.x keeps working against an upgraded database:
it reads, uploads, lists, deletes and creates containers as before. So for a database from 2.x:

1. **Upgrade the database** while the old version of the application runs, after backing it up. While
   `NameLimitsAndCaseSensitiveNames` and `UniqueLatestVersion` run, the application's requests to Resourcefull wait,
   for seconds on a small database and longer on a large one, and can time out; upgrade when traffic is light.
2. **Deploy the new version** of the application.
3. **Run the backfill** once no instance of the old version is left. Versions the old version stored in the meantime
   have no hash until then, and keep the length their caller declared.

From the moment the schema is upgraded, the old version's lookups are case-sensitive too: a name requested in another
case than it's stored in is not found, and an upload under it starts a separate name. Check
[the spellings your callers use](#case-sensitive-names) before you start.

While both versions run, two uploads of one name at the same moment can fail with a duplicate-key error, because 2.x
doesn't lock the name while it makes a version current and the upgraded database allows only one current version.
Nothing is stored wrongly; upload again. Databases from 1.x need the application stopped for
`RedesignKeysAndVersioning`, so upgrade those during the maintenance window instead.

To roll back:

- **The application only:** redeploy the old version. It works against the upgraded database, as above.
- **The database too:** restore the backup from step 1. Anything stored since the upgrade is lost, and the migrations
  have no scripts to undo them.
