# Upgrading

How to move to a newer Resourcefull release without breaking anything, and how to step back if you need to. Within one
major version, such as 3.x, upgrades are designed to be quiet: code written for an earlier release keeps compiling and
working, and the database changes only by adding to it. Coming from 1.x or 2.x? See [Upgrading to 3.0](upgrading-to-3.md).

4.0 <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> is a major release. Besides its new features, it changes the API in ways that need changes to
your code, and makes one database change that a 3.x application can't run against: see
[Upgrading to 4.0](upgrading-to-4.md).

## Upgrading from 3.0 or later

1. **Read the release's notes** in the [changelog](../CHANGELOG.md). Each says what's new, and calls out anything that
   behaves differently under "Changed".
2. **Update every Resourcefull package to the same version.** They're released together, and
   `Resourcefull.SqlServer`, `Resourcefull.InMemory` and `Resourcefull.AspNetCore` expect the `Resourcefull` of their
   own release.
3. **Upgrade the database** before the application, with `MigrateResourcefullDatabaseAsync` or the scripts in the
   package; see [Upgrading the database](providers/sql-server-upgrades.md). The table below says what each release
   adds.
4. **Deploy the application.**

### What each release changes in the database

| Release | Migrations | What they do | Before you run them |
|---|---|---|---|
| 3.1 | none | | |
| 4.0 <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `AddCopiedFrom` | adds three nullable columns recording the version a copy or restore came from | nothing |
| | `AddExpiresAt` | adds the nullable `ExpiresAt` column, and an index of the versions that expire | nothing |
| | `Resourcefull_AddStagedAt` | adds the nullable `StagedAt` column, set while an upload stores a version's content, and an index of the versions being stored | nothing |
| | `Resourcefull_DropStoredUri` | drops the `Uri` columns of `Resources` and `ResourceContainers`, whose values have gone unused since 3.0 | stop every 3.x instance: 3.x still reads and writes the columns, so it fails without them |
| | `Resourcefull_AddDeclaredLength` | adds the nullable `DeclaredLength` column, the length an [upload session](uploading-in-parts.md) said its version would have | nothing |
| | `Resourcefull_AddRetention` | adds the nullable `RetainUntil` column and the `LegalHold` column, false for every version already stored, with an index of each for the versions they [protect](deleting-safely.md) | nothing |
| | `Resourcefull_AddMetadataSearch` | adds the `ResourceMetadata` table, the index of [searchable metadata](listing.md#finding-resources-by-metadata), empty until you name keys; versions stored before then are added when you index them | nothing |
| | `Resourcefull_AddBin` | adds the nullable `DeletedAt` and `PurgeAt` columns, set while a version is in a [bin](deleting-safely.md#the-bin), with an index of each for the versions in bins | nothing |
| | `Resourcefull_SweepKeys` | rebuilds the indexes of the versions that expire and the versions in bins, adding the container and name, so a sweep can't skip a version that shares its time and version ID with another | on a large table, expect reads and writes of `Resources` to wait while the indexes build |

Every migration from 3.0 on only adds columns or indexes, and runs while the application is up, except
`Resourcefull_DropStoredUri` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> and
`Resourcefull_SweepKeys` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, which holds up the table
while it rebuilds its indexes.

## Upgrading a running application

With several instances, upgrade without downtime by changing the database first. Upgrading from 3.x to 4.0 <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>
is the exception: see [Upgrading to 4.0](upgrading-to-4.md#upgrading-the-database).

1. **Upgrade the database** while the old release runs. The old release keeps working against it: within a major
   version, the database only gains columns and indexes an older release ignores. Its
   [health check](health-checks.md) reports Degraded meanwhile, meaning "a newer release upgraded the database", which
   is expected.
2. **Roll out the new release,** instance by instance. Each reports Healthy once it's running.

## Rolling back

- **The application only,** within a major version: redeploy the earlier release. It works against the upgraded
  database, as during a rollout. Features of the newer release stop, but nothing is lost; the newer release picks up
  where it left off. A 3.x application doesn't work against a 4.0 database <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, so going back from 4.0 to
  3.x means restoring the database too.
- **The database too:** restore a backup from before the upgrade (see [Backups and recovery](backups-and-recovery.md)).
  Anything stored since is lost, so roll back the application only, unless the upgrade itself went wrong.

## Versions and support

- **Version numbers** follow [semantic versioning](https://semver.org), for all four packages together: a patch
  release fixes bugs, a minor release adds features without breaking code written for earlier releases of the same
  major version, and a major release may make breaking changes, each listed in its upgrade guide.
- **Custom providers** are covered too: the provider API changes incompatibly only in a major release, and members
  added in a minor release come with default implementations. So do members added to `IResourceContainerClient` and
  `IResourceServiceClient`. See [Writing a provider](providers/writing-a-provider.md).
- **The database** within one major version only gains what the table above lists, so any 3.x application works
  against any 3.x database at least as new as itself. A 3.x application doesn't work against a 4.0 database <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>;
  see [Upgrading to 4.0](upgrading-to-4.md#upgrading-the-database).
- **Deprecation:** an API that's going away is first marked `[Obsolete]`, with a message naming what to use
  instead, and keeps working for at least one more minor release and at least six months. It's removed only in a
  major release, listed in that release's upgrade guide. The compiler's warning is the notice, so build with
  warnings visible when you upgrade.
- **Security fixes** go into the latest minor release of the current major version; see the
  [security policy](https://github.com/zacharydf7/Resourcefull/blob/main/SECURITY.md).
- **What it runs on:** .NET 10, and SQL Server as described in [Running SQL Server](providers/sql-server-operations.md#choosing-a-sql-server).
