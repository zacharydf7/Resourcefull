# Changelog

## [Unreleased]

### Added

- **Several stores in one application.** `AddKeyedResourcefull("archive", …)` adds a store under a name, beside the one
  `AddResourcefull` adds, with its own provider and settings; resolve its client with
  `[FromKeyedServices("archive")]`. Each store has its own policies, URI provider, upload inspector, cleanup handler
  and expiry sweep. `MigrateResourcefullDatabaseAsync`, `GenerateResourcefullScript`, the SQL Server hash backfill and
  both health checks take a store's name. Spans and metrics from a named store carry `resourcefull.store`. Providers
  read the store's name from `ResourcefullOptionsBuilder.StoreName`, and a new `UseProvider` overload takes a factory.
  Signed links work with the store `AddResourcefull` adds. See [Several stores](docs/several-stores.md).
- **Listing containers a page at a time.** `ListContainersAsync` enumerates every container, or those whose names start
  with a prefix, fetching a page at a time, so it holds only one page in memory, even in a store with a container for
  each user. `GetContainerPageAsync` returns one page and a continuation token, for an API that pages for its own
  callers. Providers implement `GetContainerPageAsync` in place of `GetAllResourceContainerInfoAsync`, which returned
  every container. Each page records a `list_containers` span with `resourcefull.page_size`. See
  [Listing containers](docs/containers.md#listing-containers).
- **A schema for SQL Server's tables.** `UseSqlServer(connectionString, configure, schema: "resourcefull")` puts
  Resourcefull's tables, and its own migration history, in a schema of their own, so they don't mix with an
  application's tables in `dbo`. `MigrateResourcefullDatabaseAsync` creates the schema, and the new
  `GenerateResourcefullScript` writes the provisioning script for it, since the packaged scripts are for `dbo`. Without
  a schema nothing changes. The `migrate` span records `resourcefull.schema`. See
  [Choosing a schema](docs/providers/sql-server.md#choosing-a-schema), which also shows how to move existing tables.
- **Exporting and importing containers.** `ExportContainerAsync` writes a container, every version of every file, to
  a zip, and `ImportContainerAsync` creates a new container from one, keeping each version's ID and `LastModified`.
  Imports check the manifest and every file's hash and length, and delete what they created if anything fails.
  Providers get `ImportVersionAsync`, which returns what it stored and whose default throws `NotSupportedException`.
  With SQL Server, a version whose write hits a transient failure is retried, reading its file from the export again.
  While an import runs, its container's metadata has `resourcefull.import: in-progress`, so one that never finished can
  be found; providers get `SetContainerMetadataAsync` for it, whose default leaves imports unmarked. Exports
  read a container 1,000 names at a time, through `GetVersionPageAsync`, and write the manifest to a temporary file
  rather than holding it in memory; providers can answer it in one query, and its default asks for each name's
  versions in turn. See
  [Backing up and moving containers](docs/backup-and-move.md).
- **Signed, expiring links.** `Resourcefull.AspNetCore` can create links that let anyone download one file, without
  signing in, until they expire: `AddResourcefullLinks`, `IResourceLinkSigner.CreateTokenAsync` and
  `Results.SharedResource`. Links are sealed with ASP.NET Core Data Protection, can't be changed or read, and show the
  version that was current when they were made unless they follow the current one. See
  [Sharing files with links](docs/sharing-links.md).
- **Expiring resources.** `ResourceUploadOptions.ExpiresAt` sets when a version expires, and `UseExpiry` runs a
  background service that deletes expired versions within one interval, on every instance safely. `SetExpiryAsync`
  changes or clears a version's expiry, or the current version's with a `null` version ID. An
  `IResourceCleanupHandler` hears about every version Resourcefull deletes by itself,
  expired or pruned, before it's deleted, for auditing. With expiry on, pruning sets a version to expire five minutes
  later instead of deleting it, so a download already reading it finishes. The `AddExpiresAt` migration adds a
  nullable column and an index. Providers get three members whose defaults throw `NotSupportedException`, and
  `DeleteExpiredVersionsAsync`, which a sweep deletes each page of up to 100 expired versions through: with SQL
  Server, one transaction for the page (more when its versions hold over 256 chunks, about 10 MB, so no transaction
  logs a page of large files): 16 expired 1 MiB files take 14 round trips, where one version at a time took 98. Its
  default deletes them one at a time. See [Expiring resources](docs/expiry.md).
- **Copy, move and rename.** `CopyResourceAsync` copies a version to a name in the same or another container, as a
  new version there, and `MoveResourceAsync` moves a resource with every version, keeping their IDs; renaming is a
  move within the container. With SQL Server, no content passes through the application. A copy can be
  create-only or conditional, as an upload can. A copy or restore records
  its source in the new `ResourceInfo.CopiedFrom`. Providers get `CopyResourceAsync`, with a default implementation,
  and `MoveResourceAsync`, which throws `NotSupportedException` until implemented. The `AddCopiedFrom` migration adds
  three nullable columns. `RenameResourceAsync` renames within the container, and `IResourceContainerClient.Name`
  gives the container's name; both have default implementations. `RestoreVersionAsync` restores through
  `CopyResourceAsync`, so with SQL Server a restore
  runs within the database and is retried after a transient failure (for a version stored before 3.0, once the hash
  backfill has recorded its hash). See [Copying and moving](docs/copying-and-moving.md).
- **Container policies.** `UseContainerPolicies` sets limits for every container, and `UsePolicyProvider` gives any
  container its own, such as a quota for each user's container that an admin can change while the application runs:
  `MaxResourceSize`, `MaxContainerSize`, `MaxResourceCount`, `AllowedContentTypes` and `ReadOnly`. Sizes are
  `ResourceSize` values, such as `ResourceSize.Megabytes(100)`, or `"100 MB"` in configuration. A refused write throws
  `ResourcePolicyViolationException` and stores nothing, and `AddResourcefullExceptionHandler` turns it into 413, 409, 403
  or 415. When the policy provider fails, a container's last known policy stands in for up to 5 minutes
  (`keepLastKnownFor`). `GetUsageAsync` reports what a container holds, and `GetPolicyAsync` the limits its writes
  are checked against. Providers get `GetContainerUsageAsync` with a
  default implementation, so providers written for 3.1 keep working. See [Container policies](docs/container-policies.md).
- **Inspecting uploads.** An `IResourceUploadInspector`, registered with `UseUploadInspector`, reads each upload's
  and imported version's content while it's stored, so it can scan it for malware or check that it's the type it
  claims, and refuse it. No version becomes current until it answers; a refusal fails the upload with
  `ResourceContentRefusedException`, which `AddResourcefullExceptionHandler` turns into 422. The content is read
  once and never held whole, and with SQL Server nothing is locked while the inspector decides. Providers must read
  the content to its end before making a version current, which both built-in providers do. See
  [Inspecting uploads](docs/upload-inspection.md), with examples for ClamAV and Windows AMSI.
- **Changing metadata and content type.** `SetResourceMetadataAsync` and `SetContentTypeAsync` change a version's
  metadata or content type in place, without uploading it again: it keeps its ID, content, `LastModified` and ETag,
  and no new version is made. A new content type is checked against `AllowedContentTypes`, and a read-only container
  refuses both. `IResourceServiceClient.SetContainerMetadataAsync` replaces a container's metadata, which
  could only be set when the container was created. Providers get `SetResourceMetadataAsync` and
  `SetContentTypeAsync`, whose defaults throw `NotSupportedException`. See
  [Changing metadata and content type](docs/uploading.md#changing-metadata-and-content-type).
- **Exceptions say what they're about.** `ResourcefullException` has `ContainerName` and `ResourceName`, set by
  Resourcefull's providers and filled in by the clients for a provider that doesn't. A container conflict has no
  `ResourceName`, so it can be told from a resource conflict without reading the message. `ResourceNameException`
  has `Name`, the name that broke the rules. Every message names the container or resource it's about, in one wording.
  See [Errors](docs/errors.md#what-an-exception-is-about).
- **Settings from configuration.** `UseConfiguration(section)` reads versioning, expiry, telemetry and container
  policy settings from configuration, such as appsettings.json. Each section present turns its feature on, and a key
  that isn't a setting stops the application at startup. See
  [Settings from configuration](docs/getting-started.md#settings-from-configuration).
- **Range downloads.** A `DownloadStreamingAsync` overload takes a `ResourceRange` and reads only those bytes from
  storage, for resuming downloads and seeking in media; every download's result now has `TotalLength`, and `Range`
  for a range. `Results.Resource(container, name)` in `Resourcefull.AspNetCore` looks the resource up itself, the
  current version or the `versionId` given, and answers `Range` requests with 206 Partial Content, honoring
  `If-Range`, and 416 for a range past the end. A range past the end throws `ResourceRangeNotSatisfiableException`,
  with the content's length, which `AddResourcefullExceptionHandler` answers with 416. Providers get a range overload
  of `GetResourceStreamAsync` with a
  default implementation, so providers written for 3.1 keep working.
  See [Downloading part of a resource](docs/downloading.md#downloading-part-of-a-resource).
- **Health checks.** `AddHealthChecks().AddResourcefullCheck()` reports whether storage can be reached through any
  provider, and `AddResourcefullSqlServerCheck()` also reports Unhealthy when the database is missing migrations this
  release needs, and Degraded when a newer release has upgraded it. See [Health checks](docs/health-checks.md).
- **Telemetry.** Every client operation records a trace span and metrics, through `ActivitySource` and `Meter`
  named `Resourcefull`: each operation's duration, and the bytes uploaded and downloaded, tagged by operation,
  provider and error type. SQL Server's migrations and hash backfill record spans. Resource names, and failure
  messages that may contain them, go on spans only with `UseTelemetry(t => t.RecordResourceNames = true)`. Counters
  for failed prunes, expired versions deleted and failed, cleanup handler failures, and SQL Server's retries show work
  that doesn't fail the call that started it. Nothing is measured until something subscribes. See
  [Telemetry](docs/telemetry.md).
- **Pruning a whole container.** `PruneContainerAsync` prunes every resource in a container to `MaxVersions`, as each
  upload prunes its own name, for after an import or a failed prune. See
  [Versioning](docs/versioning.md#keeping-a-limited-number).
- **SQL Server options.** `UseSqlServer(connectionString, sql => ...)` changes EF Core's SQL Server options after
  Resourcefull's own, such as the command timeout or the retries. See [SQL Server](docs/providers/sql-server.md#retries).
- **Disposable downloads.** `ResourceDownloadResult` implements `IDisposable` and `IAsyncDisposable`, disposing its
  stream, so `await using` works on the download itself.
- **Razor Pages.** The results `TypedResults.Resource` and `TypedResults.SharedResource` return are also MVC action
  results, so a Razor Pages handler, which can't return an `IResult`, can return them. See
  [MVC, Razor Pages and Blazor](docs/aspnet-core/mvc-razor-blazor.md#razor-pages).

### Changed

- **One method per operation.** Each operation of `IResourceContainerClient` and `IResourceServiceClient` from before
  3.1 is now one method, with its optional parameters and then an optional `CancellationToken` last, rather than a pair
  of overloads where only one took a token. A token can now be named without passing `null` for the parameters
  before it: `UploadAsync("a", data, cancellationToken: token)`. `DownloadStreamingAsync`
  takes its range, and `DeleteResourceIfExistsAsync` its conditions, as optional parameters before the token, so a
  token passed by position after a version ID needs naming. `UploadAsync`'s `uploadOptions` parameter is now
  `options`. `GetResourcesAsync`, which read a whole container into memory, is removed, and with it the `list` span;
  use `ListResourcesAsync`. See [Upgrading to 4.0](docs/upgrading-to-4.md#one-method-per-operation).
- **Uploads say whether they replace.** `ResourceUploadOptions.Mode` and `ResourceCopyOptions.Mode`, a
  `ResourceWriteMode`, replace `Overwrite` and `CreateOnly`: `Create`, the default, refuses a name that exists, and
  `Replace` stores a new current version, whether or not versioning is on. With versioning on, an upload of an
  existing name is now refused unless it asks to replace. Conditions need `Replace`. `ResourceContainerPolicy` gains
  `MaxVersions`, so each container can keep its own number of versions; `UseVersioning` sets the default for the
  rest. `ResourcefullOptions.IsVersioningEnabled` is gone. `RestoreVersionAsync` works with versioning off, as long
  as the version exists. The `upload` and `copy` spans gain `resourcefull.write_mode`. `UploadFromRequestAsync` still
  replaces, as a `PUT` does. See [Upgrading to 4.0](docs/upgrading-to-4.md#uploads-say-whether-they-replace).
- **Adding Resourcefull twice throws.** A second `AddResourcefull` replaced the provider and kept the first call's other
  settings; it now throws `InvalidOperationException`. Add a second store with `AddKeyedResourcefull`. See
  [Upgrading to 4.0](docs/upgrading-to-4.md#adding-resourcefull-twice).
- **Consistent names.** The container methods drop "Resource": `CreateContainerAsync`,
  `CreateContainerIfNotExistsAsync`, `GetContainerInfoAsync`, `DeleteContainerIfExistsAsync` and
  `GetContainerClient`, beside the existing `ContainerExistsAsync`. `GetResourceContainersAsync`, which read every
  container into memory, is replaced by `ListContainersAsync`, which lists a page at a time. `ResourceContainerInfo`
  is `ContainerInfo`, and `IResourceContainerClient.ResourceContainerInfo` is `Info`. `SetUriProvider` is `UseUriProvider`.
  `ResourceUploadOptions` has `init` properties. The provider types move to `Resourcefull.Providers`,
  and the provider's container members are renamed to match. Telemetry's operation names don't change. See
  [Upgrading to 4.0](docs/upgrading-to-4.md#consistent-names).
- **Stricter names.** Uploads, copies, moves and renames, and creating or importing a container, refuse new names with
  control characters, Unicode's bidirectional controls or unpaired surrogates, and `ResourceNames.IsValidContainerName`
  and `IsValidResourceName` now return `false` for them. Names already stored stay readable and deletable, resources
  with them can be renamed, and exports of them can be imported. See [Names](docs/names.md).
- **The stored `Uri` columns are dropped.** The `Resourcefull_DropStoredUri` migration drops `Uri` from `Resources`
  and `ResourceContainers`; `Uri` comes from your URI provider on every read, as it has since 3.0. 3.x applications
  still read and write the columns, so they fail against an upgraded database: stop them before migrating, and roll
  back by restoring a backup. See [Upgrading to 4.0](docs/upgrading-to-4.md#upgrading-the-database).
- **Providers derive from the `ResourceRepository` base class,** which replaces the `IResourceRepository` interface.
  Its members take a `ResourceLocation` rather than a container name and a resource name, return `IReadOnlyList<T>`
  rather than `List<T>`, and take metadata as `IReadOnlyDictionary<string, string>`. A write is given a
  `NewResourceVersion` rather than a `ResourceInfo`; it holds only what the write's caller can know. `AddResourceAsync`
  and `DeleteResourceAsync` each have one overload, taking an optional condition, so every provider decides
  conditional writes itself. Providers no longer store `Uri`. See [Upgrading to 4.0](docs/upgrading-to-4.md#custom-providers).
- **`ResourceInfo` can't be changed:** it's a sealed record with `init`-only properties, and its `Metadata` is an
  `IReadOnlyDictionary<string, string>`. Change a stored version with `SetResourceMetadataAsync` or
  `SetContentTypeAsync`, or make a changed copy with `with`. See [Upgrading to 4.0](docs/upgrading-to-4.md).
- **`IResourceContainerClient` isn't `IDisposable`,** since its `Dispose` did nothing; remove `using` where you call
  `GetContainerClient`.
- **Uploads to SQL Server hold nothing while their content arrives.** An upload stores its version staged, hidden
  from every read, then its content a MiB at a time, each batch committing on its own on a connection it gives back,
  and makes the version current in a short transaction at the end. So a slow client no longer holds a connection,
  locks and the transaction log for as long as it takes, and a transient failure partway sends the batch again rather
  than failing the upload, even from a stream that can't seek, such as a request body. A canceled or failed upload
  deletes what it stored; the expiry sweep deletes what an upload whose process died left behind, an hour after it
  last stored anything. Each upload costs about one round trip more. The `Resourcefull_AddStagedAt` migration adds a
  nullable column and an index. Providers get `DeleteAbandonedUploadsAsync`, whose default does nothing. The upload
  span gains `resourcefull.batches`, and the `expire` span `resourcefull.abandoned_uploads`. See
  [Uploads, connections and the log](docs/providers/sql-server-operations.md#uploads-connections-and-the-log).
- **`UseVersioning` with fewer than one version** throws `ArgumentOutOfRangeException`, as `UseExpiry` and
  `UseContainerPolicies` do for a value out of range, rather than `InvalidOperationException`.
- **The core package has three new dependencies,** for features new in this release:
  `Microsoft.Extensions.Caching.Memory` (container policies' cache), `Microsoft.Extensions.Diagnostics.HealthChecks`
  (`AddResourcefullCheck`) and `Microsoft.Extensions.Hosting.Abstractions` (the `UseExpiry` sweep). ASP.NET Core
  applications already have them; others take them on upgrading.
- **Downloads are safer by default.** `Results.Resource` sends `Cache-Control: private, no-cache`, so shared caches
  such as CDNs no longer keep files (set `ResourceResultOptions.CacheControl` to cache public files), and
  `X-Content-Type-Options: nosniff`. `Inline` now displays only types that are safe to show (common image formats
  (PNG, JPEG, GIF, WebP, AVIF and BMP; not SVG), PDF, audio, video and plain text) and offers anything else as a
  download; `InlineContentTypes` changes the list. See [Serving downloads](docs/aspnet-core/serving-downloads.md).
- **Deleting a resource with many versions on SQL Server** happens in batches that each commit, oldest first and the
  current version last, rather than in one transaction, so it no longer holds the whole history in one transaction. A
  delete that's canceled or fails partway leaves the rest whole, and calling it again finishes it. Deleting a large
  version also no longer locks the whole content table while it runs. See
  [Versioning](docs/versioning.md#deleting).
- **A new look.** The packages have a new icon, and the [documentation site](https://zacharydf7.github.io/Resourcefull/)
  has a new design, a home page, and a sidebar grouped by what you're trying to do. Pages that covered several topics
  are split into one page each; links to the old pages still work.
- **`ResourceInfo.VersionId` is non-nullable,** since every stored version has one, so passing it to methods that
  take a version ID no longer needs `!`. Code that checked it for `null` still compiles. It's also `required`, so a
  provider must set it on every `ResourceInfo` it builds.
- **Deleting a large container on SQL Server is faster:** each batch deletes its versions in one round trip without
  loading them first. A container of a million files deletes in about a third less time, with about 40% fewer round
  trips and allocations.
- **SQL Server stores content in about 11% less space.** New content goes in 40,200-byte chunks, which fill SQL
  Server's pages exactly; the 65,535-byte chunks of earlier releases also took a page of pointers each. Content already
  stored keeps its chunks and stays readable. Uploads also allocate about half the memory they did. See
  [How SQL Server stores files](docs/providers/sql-server-storage.md).

### Fixed

- **Creating or deleting a container on SQL Server** no longer reports failure after succeeding. When the database
  committed but the connection failed before saying so, the automatic retry ran again and reported a conflict for a
  create, or a concurrency error for a delete; now it recognizes its own success, as uploads already did.
- **A file moved out of a container while the container was being deleted on SQL Server** could be deleted from its
  new container. The delete now removes only what's still in the container it's deleting.
- **A write on SQL Server whose wait for its name's write lock lost a deadlock or timed out** failed with "Could not
  lock the resource name for writing" instead of being retried, as any other deadlock is. It now raises error 50001,
  which the retry policy retries.
- **A download's file name leaves out control characters and the characters that change text direction,** which a
  resource name can hold. A right-to-left override (U+202E) could make a download of `invoice` + U+202E + `fdp.exe`
  show as `invoiceexe.pdf`, and a name with a line break failed with an error instead of downloading.
- **The library's own awaits no longer risk a deadlock when the caller blocks on a call.** A desktop app, or any other app
  with a synchronization context, could hang when it waited for a call with `.Result` or `.Wait()`, because the
  library's own awaits resumed on that context. They now resume on the thread pool.

## [3.1.0] - 2026-10-29

New features, all additive: code written for 3.0 keeps compiling, and custom providers keep working. The one thing to
check is a test that expects exactly `ArgumentException` for a bad name; it's now the subclass
`ResourceNameException`.

### Added

- **Conditional uploads and deletes.** `ResourceUploadOptions.Conditions` and a `DeleteResourceIfExistsAsync`
  overload take `ResourceConditions`, so a write goes ahead only if the name's current version still has the version
  ID or content hash you read. The write decides it, so of concurrent writers that read the same version exactly one
  succeeds; the rest throw the new `ResourcePreconditionFailedException` (HTTP 412). See
  [Updating only if unchanged](docs/concurrency.md#updating-only-if-unchanged).
- **`Resourcefull.AspNetCore`**, a new package for serving and accepting files. `Results.Resource(download)` sends a
  download with `Content-Length`, a strong `ETag`, `Last-Modified` and `Content-Disposition`, and answers conditional
  and `HEAD` requests. `UploadFromRequestAsync` stores a request body, honoring `If-Match`, `If-None-Match` and
  `If-Unmodified-Since` race-free. `AddResourcefullExceptionHandler` turns Resourcefull's exceptions into problem
  details with the right status codes. See [ASP.NET Core](docs/aspnet-core/index.md).
- **`ResourceNameException`**, thrown for a name that breaks the name rules. It derives from `ArgumentException`, so
  existing `catch (ArgumentException)` code still catches it, but a test that expects exactly `ArgumentException`
  needs updating.
- **Listing by prefix, a page at a time.** `ListResourcesAsync` enumerates a container's current resources, or those
  under a prefix, fetching pages as you go, and `GetResourcePageAsync` returns one page with a continuation token for
  APIs that page for their own callers. With SQL Server a page costs the same wherever it starts. See
  [Listing resources](docs/listing.md).
- **For provider authors:** `IResourceRepository.GetResourcePageAsync`, whose default implementation sorts and filters
  the full listing, so providers written for 3.0 keep working until they answer it directly.
- **For provider authors:** conditional overloads of `IResourceRepository.AddResourceAsync` and
  `DeleteResourceAsync`, with default implementations, so providers written for 3.0 keep compiling and refuse
  conditional writes with `NotSupportedException`.

### Fixed

- With SQL Server, a delete whose commit succeeded but whose acknowledgement was lost to a transient error, and was
  retried, reported that nothing was deleted. It now reports the delete.

## [3.0.0] - 2026-10-29

The first release of Resourcefull for general use. Coming from an earlier release, read
[Upgrading to 3.0](docs/upgrading-to-3.md) first: it lists every change to code, behavior and the database, with what each
needs from you. Items marked *(from 1.x)* were already in 2.x.

### Added

- **Content hashes.** Every version records the SHA-256 of its content, measured as it's written, as
  `ResourceInfo.ContentHash`. `ContentHashBackfill` records hashes for versions stored earlier.
- **Measured lengths.** `ContentLength` is measured from the stored content, and an upload whose declared length
  differs throws `ResourceContentLengthMismatchException` and stores nothing.
- **Cancellation.** Every method that contacts storage has an overload that takes a `CancellationToken`. *(from 1.x)*
- **Create-only uploads** with `ResourceUploadOptions.CreateOnly`, decided by the write, so concurrent ones can't both
  succeed.
- **Provisioning from code** with `MigrateResourcefullDatabaseAsync`, into any database the connection string names.
- **Writing a provider.** `IResourceRepository` is public, with a documented contract.
- **Name checks.** `ResourceNames.IsValidContainerName` and `IsValidResourceName` check a name against the rules
  before you use it.
- **Logging.** A version prune that fails after its upload was stored is logged as a warning.
- **Documentation** for every concept, both providers, ASP.NET Core and upgrading, and an API reference generated from
  the XML documentation that ships in the packages.

### Changed

- Methods return values and throw exceptions instead of returning `ErrorOr`, and several were renamed. *(from 1.x)*
- Names are case-sensitive and limited to 128 characters for a container and 256 for a resource, with no whitespace at
  either end.
- Deleting a name without a version ID deletes all its versions.
- New version IDs are GUIDs (older ones stay valid), to compare rather than parse, and storage decides which version is
  current and the order versions are listed in.
- Uploads read from the stream's current position, without rewinding it first.
- `LastModified` is when the version became current, by the storing server's clock.
- Download streams can't seek, and with SQL Server, a download whose version is deleted midway throws `IOException`.
- `Uri` comes from the URI provider whenever a container or version is returned, so a changed base URL applies to
  everything already stored.
- The SQL Server package's EF Core migration classes are internal.
- SQL Server provisioning scripts no longer name a database, and ship in the package's `scripts` folder rather than
  being added to consuming projects.

### Fixed

- Concurrent uploads of one name always leave exactly one current version.
- Uploads and downloads of any size use a fixed amount of memory, and uploads make about half as many round trips.
- An upload into a container deleted moments before reports the missing container. *(from 1.x)*
- Deleting a large container works in bounded batches, and can be resumed if it stops partway.

[Unreleased]: https://github.com/zacharydf7/Resourcefull/compare/v3.1.0...HEAD
[3.1.0]: https://github.com/zacharydf7/Resourcefull/compare/v3.0.0...v3.1.0
[3.0.0]: https://github.com/zacharydf7/Resourcefull/releases/tag/v3.0.0
