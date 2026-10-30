# Upgrading

How to move to a newer Resourcefull release without breaking anything, and how to step back if you need to. Within one
major version, such as 3.x, upgrades are designed to be quiet: code written for an earlier release keeps compiling and
working, and the database changes only by adding to it. Coming from 1.x or 2.x? See [Upgrading to 3.0](upgrading-to-3.md).

## Upgrading between 3.x releases

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
| 3.2 <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> | `AddCopiedFrom` | adds three nullable columns recording the version a copy or restore came from | nothing |
| | `AddExpiresAt` | adds the nullable `ExpiresAt` column, and an index of the versions that expire | nothing |

Every 3.x migration only adds columns or indexes, and runs while the application is up.

## Upgrading a running application

With several instances, upgrade without downtime by changing the database first:

1. **Upgrade the database** while the old release runs. The old release keeps working against it: within 3.x, the
   database only gains columns and indexes the old release ignores. Its [health check](health-checks.md) reports
   Degraded meanwhile, meaning "a newer release upgraded the database", which is expected.
2. **Roll out the new release,** instance by instance. Each reports Healthy once it's running.

## Rolling back

- **The application only:** redeploy the earlier 3.x release. It works against the upgraded database, as during a
  rollout. Features of the newer release stop: for example, a 3.1 application ignores the expiry times 3.2 stored, so
  expired files stay until a 3.2 application deletes them again. Nothing is lost; the newer release picks up where it
  left off.
- **The database too:** restore a backup from before the upgrade (see [Backups and recovery](backups-and-recovery.md)).
  Anything stored since is lost, so roll back the application only, unless the upgrade itself went wrong.

## Versions and support

- **Version numbers** follow [semantic versioning](https://semver.org), for all four packages together: a patch
  release fixes bugs, a minor release adds features without breaking code written for earlier releases of the same
  major version, and a major release may make breaking changes, each listed in its upgrade guide.
- **Custom providers** are covered too: `IResourceRepository` changes incompatibly only in a major release, and members
  added in minor releases come with default implementations. See [Members added since 3.0](providers/newer-members.md).
- **The database** within one major version only gains what the table above lists, so any 3.x application works
  against any 3.x database at least as new as itself.
- **Security fixes** go into the latest minor release of the current major version; see the
  [security policy](https://github.com/zacharydf7/Resourcefull/blob/main/SECURITY.md).
- **What it runs on:** .NET 10, and SQL Server as described in [Running SQL Server](providers/sql-server-operations.md#choosing-a-sql-server).
