# Upgrading the database

New releases of `Resourcefull.SqlServer` can add to the database's schema. This page covers bringing a
database up to date, and recording hashes for files stored before the database kept them. Moving from a release
before 3.0 has steps of its own; see [Upgrading to 3.0](../upgrading.md).

## Upgrading the schema

New releases can add migrations. Before upgrading the package in production:

1. Check the [upgrade guide](../upgrading.md#upgrading-the-database) for migrations that need preparation.
2. Back up the database.
3. Apply the migrations, with `MigrateResourcefullDatabaseAsync` or the scripts, during a maintenance window if the
   upgrade guide says a migration needs one.

To apply the scripts step by step, run the ones under `scripts/Migrations/` that the database is missing, oldest
first, with `sqlcmd -b -I` (or SSMS in SQLCMD mode with `:on error exit` at the top; otherwise SSMS carries on after an
error). Each script applies one migration, skips itself when `__EFMigrationsHistory` already records it, and is safe to
run again if it's interrupted. `Resourcefull.sql` applies all of them in one go.

## Recording hashes for older versions

Every version stored by 3.0 or later records its SHA-256 hash. Versions stored earlier have `ContentHash = null` until
`ContentHashBackfill`, which `UseSqlServer` registers, hashes them:

```csharp
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
