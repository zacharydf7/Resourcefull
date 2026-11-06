# Upgrading the database

New releases of `Resourcefull.SqlServer` can add to the database's schema. This page covers bringing a
database up to date, and recording hashes for files stored before the database kept them.

## Upgrading the schema

New releases can add migrations. To upgrade in production:

1. Read the release's notes in the [changelog](../../CHANGELOG.md).
2. In your project, update every Resourcefull package to the same version, since each expects the `Resourcefull` package
   from its own release.
3. Back up the database.
4. Apply the migrations, with `MigrateResourcefullDatabaseAsync` or the scripts, during a maintenance window if the notes
   say a migration needs one.

To apply the scripts step by step, run the ones under `scripts/Migrations/` that the database is missing, oldest
first, with `sqlcmd -b -I` (or SSMS in SQLCMD mode with `:on error exit` at the top; otherwise SSMS carries on after an
error). Each script applies one migration, skips itself when `__EFMigrationsHistory` already records it, and is safe to
run again if it's interrupted. `Resourcefull.sql` applies all of them in one go.

With the tables in a [schema](sql-server.md#choosing-a-schema), the packaged scripts won't work, since they're
written for tables in the default schema; generate a script with `GenerateResourcefullScript` from the upgraded
application instead. Like `Resourcefull.sql`, it applies whatever the
database is missing.

## Upgrading a running application

With several instances, upgrade without downtime by changing the database first:

1. **Upgrade the database** while the old release runs. Within a major version, the database only gains columns and
   indexes an older release ignores, so the old release keeps working against it. Its
   [health check](../health-checks.md) reports Degraded meanwhile, meaning "a newer release upgraded the database", which
   is expected.
2. **Roll out the new release,** instance by instance. Each reports Healthy once it's running.

## Rolling back

- **The application only,** within a major version: redeploy the earlier release. It works against the upgraded
  database, as during a rollout. Features of the newer release stop, but nothing is lost; the newer release picks up
  where it left off.
- **The database too:** restore a backup from before the upgrade (see [Backups and recovery](../backups-and-recovery.md)).
  Anything stored since is lost, so roll back the application only, unless the upgrade itself went wrong.

## A database from a release before 4.0

4.0 can upgrade a database an earlier release created, but it takes a short outage rather than a rollout, because the
earlier release can't run against the upgraded database. Back up the database, stop every instance (workers and scheduled
jobs included), apply the migrations, and deploy the new application. An instance left on the earlier release fails with
error 207, "Invalid column name 'Uri'": stop it. To go back, restore the backup.

A database from before 3.0 needs a longer window, because its migrations rebuild tables. Before you start, rename or
delete any name with whitespace at either end, and any empty container name: after the upgrade, no method accepts them.
Names become case-sensitive, and the versions of a name stored in different cases all take one spelling. A container name
over 128 characters or a resource name over 256 stops the upgrade at that migration. That migration changes nothing, but
earlier ones stay applied, so restore the backup to go back. Once the upgrade finishes,
[record the hashes](#recording-hashes-for-older-versions) of the database's older files.

## Recording hashes for older versions

Every version stored by 3.0 or later records its SHA-256 hash. Versions stored earlier have `ContentHash = null` until
`ContentHashBackfill`, which `UseSqlServer` registers, hashes them:

```csharp
using Resourcefull.SqlServer;

using IServiceScope scope = app.Services.CreateScope();
ContentHashBackfill backfill = scope.ServiceProvider.GetRequiredService<ContentHashBackfill>();
ContentHashBackfillResult result = await backfill.RunAsync(cancellationToken);

logger.LogInformation("Hashed {Count} versions", result.Hashed);
foreach (ContentLengthMismatch mismatch in result.Mismatches)
{
    logger.LogWarning("{Container}/{Name} {Version}: recorded {Recorded} bytes, stored {Stored}",
        mismatch.ContainerName, mismatch.Name, mismatch.VersionId, mismatch.RecordedLength, mismatch.StoredLength);
}
```

- **It reads each unhashed version's content once**, so run it when the load allows. The application can keep running
  meanwhile.
- **It's resumable.** It saves each hash as soon as it's computed, so you can cancel it at any time, and the next run
  carries on where it stopped. Once every version has a hash, a run reads no content.
- **It reports every length mismatch on every run.** Earlier releases recorded the length the caller declared rather
  than measuring it, so a version's `ContentLength` can be wrong. The backfill hashes such versions anyway and keeps
  reporting them until you correct their `ContentLength`.
