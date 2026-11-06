# Changelog

## Unreleased

The first release this changelog covers, and the first under the Resourcefull License: free to use, including in
commercial projects, with no rights to the source. Versions before 4.0 were released under the MIT license, which still
applies to them. A database from an earlier release can be upgraded: see
[Upgrading the database](docs/providers/sql-server-upgrades.md#a-database-from-a-release-before-40).

### Where files live

- **[SQL Server](docs/providers/sql-server.md), [PostgreSQL](docs/providers/postgresql.md) and
  [SQLite](docs/providers/sqlite.md)**, or **[a directory on a disk or file share](docs/providers/file-system.md)**, on
  infrastructure you run yourself.
- **[Amazon S3 or another S3-compatible store](docs/providers/s3.md)** and **[Azure Blob Storage](docs/providers/azure-blob.md)**,
  with every feature, so moving to the cloud takes changing one line. [Copying a store](docs/moving-providers.md) moves
  what's already stored.
- **[In memory](docs/providers/in-memory.md)** for tests, and **[your own provider](docs/providers/writing-a-provider.md)**,
  checked by the contract tests in `Resourcefull.Testing`.
- **[Several stores](docs/several-stores.md)** in one application.

### Working with files

- **[Versions](docs/versioning.md)**, with exactly one current version for each name, even when uploads run at once.
- **[Streaming uploads](docs/uploading.md) and [downloads](docs/downloading.md)** of any size, with memory use that stays
  flat, and with content hashes, [ranges](docs/downloading.md#downloading-part-of-a-resource) and [conditions](docs/concurrency.md).
- **[Uploading in parts](docs/uploading-in-parts.md):** what's arrived survives a dropped connection or a restart.
- **[Copy, move and rename](docs/copying-and-moving.md)**, with each copy remembering where it came from.
- **[Listing](docs/listing.md)** a page at a time, and finding files by metadata.
- **[Expiring files](docs/expiry.md)** that delete themselves, with a hook to audit everything Resourcefull deletes.
- **[A bin, retention dates and legal holds](docs/deleting-safely.md)** for files that mustn't be lost.
- **[Container policies](docs/container-policies.md):** limits on file size, total size and file count, allowed file
  types and read-only containers, which your application can change while it runs.
- **[Inspecting uploads](docs/upload-inspection.md)** before they're stored, to scan for malware or check their type.
- **[Backups](docs/backup-and-move.md):** export a container, every version of every file, to a zip, and import it.

### ASP.NET Core

- **[Serving downloads](docs/aspnet-core/serving-downloads.md) and [accepting uploads](docs/aspnet-core/accepting-uploads.md)**
  with ETags, preconditions and ranges, and [exceptions turned into status codes](docs/aspnet-core/status-codes.md).
- **[Download links](docs/sharing-links.md) and [upload links](docs/upload-links.md)** that work without signing in, until
  they expire.
- **[Resumable uploads](docs/aspnet-core/resumable-uploads.md)** over tus, for Uppy and tus-js-client.
- **[A dashboard](docs/aspnet-core/dashboard.md)** showing health, usage against limits, files and their versions.

### Running it

- **[Telemetry](docs/telemetry.md)** for every operation, through OpenTelemetry, and **[health checks](docs/health-checks.md)**.
- **[Provisioning and upgrades](docs/providers/sql-server-upgrades.md)** through migrations or scripts.
