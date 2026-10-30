# Releases and roadmap

What each release brought, in a few lines, and what's planned next. For every change, including the small ones, see
the [changelog](../CHANGELOG.md).

## Coming in 3.2

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

The biggest release so far, documented here already:

- **[Copy, move and rename](copying-and-moving.md)** files, within a container or between containers. Each copy
  remembers where it came from.
- **[Container policies](container-policies.md):** per-container limits on file size, total size and file count,
  allowed file types, and read-only containers, which your application can change while it runs.
- **[Expiring files](expiry.md)** that delete themselves, with a hook to audit everything Resourcefull deletes.
- **[Signed links](sharing-links.md)** that let anyone download one file, without signing in, until they expire.
- **[Backups and moves](backup-and-move.md):** export a container, every version of every file, to a zip, and import
  it into another store.
- **[Range downloads](downloading.md#downloading-part-of-a-resource)**, so videos can seek and downloads can resume.
- **[Health checks](health-checks.md) and [telemetry](telemetry.md)** for every operation, through OpenTelemetry.
- **Safer downloads by default:** files aren't kept by shared caches, and only safe types display in the browser.
- **Fixes:** with expiry turned on, a download in progress finishes even when the file is replaced; creating or deleting
  a container on SQL Server no longer reports failure after succeeding.

## 3.1, October 2026

- **[ASP.NET Core support](aspnet-core/index.md)**, a new package: serve downloads with the right headers, accept
  uploads with preconditions, and turn errors into status codes.
- **[Conditional writes](concurrency.md#updating-only-if-unchanged):** upload or delete only if the file hasn't changed
  since you read it, so two editors can't overwrite each other.
- **[Listing a page at a time](listing.md)**, and by name prefix, for containers of any size.
- **Fix:** with SQL Server, a delete that succeeded no longer reported that nothing was deleted when a transient error
  hid its success.

See the [3.1 changelog](../CHANGELOG.md#310---2026-10-29).

## 3.0, October 2026

The first release for general use:

- **[Files of any size](large-files.md)** stream in and out with flat memory use.
- **[Every version is measured and hashed](uploading.md)** as it's written, so what you read back is what was stored.
- **[Create-only uploads](concurrency.md)**, decided by the write itself, so of several at once exactly one succeeds.
- **[Your own storage](providers/writing-a-provider.md):** the provider interface is public, with a documented
  contract and tests to check yours against.
- **[Cancellation](cancellation.md)** for every call that contacts storage.
- **Fixes:** concurrent uploads of one name always leave exactly one current version, and large containers delete in
  bounded batches that can resume.

Coming from 1.x or 2.x? See [Upgrading to 3.0](upgrading-to-3.md). See the
[3.0 changelog](../CHANGELOG.md#300---2026-10-29).

## What's next

These are plans, not promises: the order can change, and nothing has a date. Each links to its issue on GitHub, where a
comment saying you need it helps decide what comes first.

- **More places to store files:** a [PostgreSQL provider](https://github.com/zacharydf7/Resourcefull/issues/108), an
  [S3-compatible provider](https://github.com/zacharydf7/Resourcefull/issues/110), a
  [file-system provider](https://github.com/zacharydf7/Resourcefull/issues/51) and a
  [SQLite provider](https://github.com/zacharydf7/Resourcefull/issues/118).
- **Bigger uploads:** [resumable uploads](https://github.com/zacharydf7/Resourcefull/issues/112),
  [signed upload links](https://github.com/zacharydf7/Resourcefull/issues/113), and
  [staged uploads](https://github.com/zacharydf7/Resourcefull/issues/84) that keep large files from holding a database
  transaction open.
- **Managing files:** [changing metadata without re-uploading](https://github.com/zacharydf7/Resourcefull/issues/99),
  [finding files by metadata](https://github.com/zacharydf7/Resourcefull/issues/57),
  [soft delete](https://github.com/zacharydf7/Resourcefull/issues/56), and
  [storing identical content once](https://github.com/zacharydf7/Resourcefull/issues/58).
- **Seeing your storage:** a [dashboard page](https://github.com/zacharydf7/Resourcefull/issues/129) for containers,
  usage and limits.
- **4.0,** further out: a simpler API with
  [one method per operation](https://github.com/zacharydf7/Resourcefull/issues/125) and
  [more predictable writes](https://github.com/zacharydf7/Resourcefull/issues/126).

Something missing? [Open an issue](https://github.com/zacharydf7/Resourcefull/issues/new/choose).
