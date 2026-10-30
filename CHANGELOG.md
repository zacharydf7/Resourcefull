# Changelog

## Unreleased

### Added

- **Exporting and importing containers.** `ExportContainerAsync` writes a container, every version of every file, to
  a zip, and `ImportContainerAsync` creates a new container from one, keeping each version's ID and `LastModified`.
  Imports check the manifest and every file's hash, and delete what they created if anything fails. Providers get
  `ImportVersionAsync`, which returns what it stored and whose default throws `NotSupportedException`. See
  [Backing up and moving containers](docs/backup-and-move.md).
- **Signed, expiring links.** `Resourcefull.AspNetCore` can create links that let anyone download one file, without
  signing in, until they expire: `AddResourcefullLinks`, `IResourceLinkSigner.CreateTokenAsync` and
  `Results.SharedResource`. Links are sealed with ASP.NET Core Data Protection, can't be changed or read, and show the
  version that was current when they were made unless they follow the current one. See
  [Sharing files with links](docs/sharing-links.md).
- **Expiring resources.** `ResourceUploadOptions.ExpiresAt` sets when a version expires, and `UseExpiry` runs a
  background service that deletes expired versions within one interval, on every instance safely. `SetExpiryAsync`
  changes or clears an expiry. An `IResourceCleanupHandler` hears about every version Resourcefull deletes by itself,
  expired or pruned, before it's deleted, for auditing. The `AddExpiresAt` migration adds a nullable column and an
  index. Providers get three members whose defaults throw `NotSupportedException`. See
  [Expiring resources](docs/expiry.md).
- **Copy, move and rename.** `CopyResourceAsync` copies a version to a name in the same or another container, as a
  new version there, and `MoveResourceAsync` moves a resource with every version, keeping their IDs; renaming is a
  move within the container. With SQL Server, no content passes through the application. A copy or restore records
  its source in the new `ResourceInfo.CopiedFrom`. Providers get `CopyResourceAsync`, with a default implementation,
  and `MoveResourceAsync`, which throws `NotSupportedException` until implemented. The `AddCopiedFrom` migration adds
  three nullable columns. See [Copying and moving](docs/copying-and-moving.md).
- **Container policies.** `UseContainerPolicies` sets limits for every container, and `UsePolicyProvider` gives any
  container its own, such as a quota for each user's container that an admin can change while the application runs:
  `MaxResourceSize`, `MaxContainerSize`, `MaxResourceCount`, `AllowedContentTypes` and `ReadOnly`. Sizes are
  `ResourceSize` values, such as `ResourceSize.Megabytes(100)`, or `"100 MB"` in configuration. A refused write throws
  `ResourcePolicyViolationException` and stores nothing, and `AddResourcefullExceptionHandler` turns it into 413, 409, 403
  or 415. `GetUsageAsync` reports what a container holds. Providers get `GetContainerUsageAsync` with a default
  implementation, so providers written for 3.1 keep working. See [Container policies](docs/container-policies.md).
- **Range downloads.** A `DownloadStreamingAsync` overload takes a `ResourceRange` and reads only those bytes from
  storage, for resuming downloads and seeking in media; every download's result now has `TotalLength`, and `Range`
  for a range. `Results.Resource(container, name)` in `Resourcefull.AspNetCore` looks the resource up itself and
  answers `Range` requests with 206 Partial Content, honoring `If-Range`, and 416 for a range past the end. Providers
  get a range overload of `GetResourceStreamAsync` with a default implementation, so providers written for 3.1 keep
  working. See [Downloading part of a resource](docs/downloading.md#downloading-part-of-a-resource).
- **Health checks.** `AddHealthChecks().AddResourcefullCheck()` reports whether storage can be reached through any
  provider, and `AddResourcefullSqlServerCheck()` also reports Unhealthy when the database is missing migrations this
  release needs, and Degraded when a newer release has upgraded it. See [Health checks](docs/health-checks.md).
- **Telemetry.** Every client operation records a trace span and metrics, through `ActivitySource` and `Meter`
  named `Resourcefull`: each operation's duration, and the bytes uploaded and downloaded, tagged by operation,
  provider and error type. SQL Server's migrations and hash backfill record spans. Resource names, and failure
  messages that may contain them, go on spans only with `UseTelemetry(t => t.RecordResourceNames = true)`. Nothing is
  measured until something subscribes. See [Telemetry](docs/telemetry.md).

### Changed

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

## 3.1.0

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

## 3.0.0

The first release of Resourcefull for general use. Coming from an earlier release, read
[Upgrading to 3.0](docs/upgrading.md) first: it lists every change to code, behavior and the database, with what each
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
