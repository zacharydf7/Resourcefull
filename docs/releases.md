# Releases and roadmap

What each release brought, in a few lines, how versions and support work, and what's planned next. For each release's
full notes, see the [changelog](../CHANGELOG.md).

## 4.0

- **[Six places to store files](providers/features.md):** [SQL Server](providers/sql-server.md),
  [PostgreSQL](providers/postgresql.md),
  [SQLite](providers/sqlite.md), [a directory on a disk or file share](providers/file-system.md),
  [Amazon S3 or another S3-compatible store](providers/s3.md) and [Azure Blob Storage](providers/azure-blob.md), each
  with every feature, so moving from your own servers to the cloud takes changing one line.
- **[Uploading in parts](uploading-in-parts.md):** a large file arrives over several requests, and what's arrived
  survives a dropped connection or a restart.
- **[Upload links](upload-links.md)** that let someone without an account send you files, until they expire.
- **[A dashboard](aspnet-core/dashboard.md)** for browsing containers, usage and limits while you develop.
- **[Versions](versioning.md), [files of any size](large-files.md) and [conditional writes](concurrency.md),** with
  exactly one current version for each name.
- **[ASP.NET Core support](aspnet-core/index.md):** serve downloads with the right headers, accept uploads with
  preconditions, and turn errors into status codes.
- **[Finding files by metadata](listing.md#finding-resources-by-metadata)**, and a [bin, retention dates and legal
  holds](deleting-safely.md) for files that mustn't be lost.
- **[The same name rules](names.md)** with every provider.
- **[Copy, move and rename](copying-and-moving.md)** files, within a container or between containers. Each copy
  remembers where it came from.
- **[Container policies](container-policies.md):** per-container limits on file size, total size and file count,
  allowed file types, and read-only containers, which your application can change while it runs.
- **[Expiring files](expiry.md)** that delete themselves, with a hook to audit everything Resourcefull deletes.
- **[Signed links](sharing-links.md)** that let anyone download one file, without signing in, until they expire.
- **[Backups and moves](backup-and-move.md):** export a container, every version of every file, to a zip, and import
  it into another store.
- **[Range downloads](downloading.md#downloading-part-of-a-resource)**, so videos can seek and downloads can resume.
- **[Inspecting uploads](upload-inspection.md)** before they're stored, to scan them for malware or check their type.
- **[Changing metadata and content type](uploading.md#changing-metadata-and-content-type)** without uploading again,
  and changing a container's metadata.
- **[Slow uploads that hold nothing up](providers/sql-server-operations.md#uploads-connections-and-the-log):** with SQL
  Server, an upload's content goes in about a MiB at a time, each batch committed on its own, so a slow client ties up
  no lock or connection while it sends.
- **[Health checks](health-checks.md) and [telemetry](telemetry.md)** for every operation, through OpenTelemetry.
- **Safe downloads by default:** files aren't kept by shared caches, and only safe types display in the browser.

## Versions and support

- **Version numbers** follow [semantic versioning](https://semver.org), for all the packages together: a patch release
  fixes bugs, a minor release adds features without breaking code written for earlier releases of the same major
  version, and a major release may make breaking changes, each listed in the [changelog](../CHANGELOG.md).
- **Custom providers** are covered too: the provider API changes incompatibly only in a major release, and members
  added in a minor release come with default implementations. So do members added to `IResourceContainerClient` and
  `IResourceServiceClient`. See [Writing a provider](providers/writing-a-provider.md).
- **The database** within one major version only gains columns and indexes an older release ignores, so any release
  works against a database of the same major version at least as new as itself. See
  [Upgrading the database](providers/sql-server-upgrades.md).
- **Deprecation:** an API that's going away is first marked `[Obsolete]`, with a message naming what to use instead, and
  keeps working for at least one more minor release and at least six months. It's removed only in a major release, listed
  in the changelog. The compiler's warning is the notice, so build with warnings visible when you upgrade.
- **Security fixes** go into the latest minor release of the current major version.
- **What it runs on:** .NET 10, and SQL Server as described in
  [Running SQL Server](providers/sql-server-operations.md#choosing-a-sql-server).

## What's next

These are plans, not promises: the order can change, and nothing has a date.

- **Managing files:** storing identical content once.
