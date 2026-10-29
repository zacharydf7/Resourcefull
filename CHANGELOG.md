# Changelog

## Unreleased

### Added

- **Conditional uploads and deletes.** `ResourceUploadOptions.Conditions` and a `DeleteResourceIfExistsAsync`
  overload take `ResourceConditions`, so a write goes ahead only if the name's current version still has the version
  ID or content hash you read. The write decides it, so of concurrent writers that read the same version exactly one
  succeeds; the rest throw the new `ResourcePreconditionFailedException` (HTTP 412). See
  [Updating only if unchanged](docs/concurrency.md#updating-only-if-unchanged).
- **Listing by prefix, a page at a time.** `ListResourcesAsync` enumerates a container's current resources, or those
  under a prefix, fetching pages as you go, and `GetResourcePageAsync` returns one page with a continuation token for
  APIs that page for their own callers. With SQL Server a page costs the same wherever it starts. See
  [Listing resources](docs/containers-and-names.md#listing-resources).
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
