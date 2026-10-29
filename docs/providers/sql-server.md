# SQL Server

`Resourcefull.SqlServer` stores resources in a SQL Server database, splitting content into chunks so files of any size
stream in and out with flat memory use. It's tested against SQL Server 2022 and LocalDB.

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!));
```

## Choosing the database

The connection string names the database that holds Resourcefull's tables: `ResourceContainers`, `Resources`,
`ResourceChunks` and EF Core's `__EFMigrationsHistory`. It can be a database of its own or your application's, as long
as none of your tables share those names.

A database of its own is the better default. Provisioning turns on [`READ_COMMITTED_SNAPSHOT`](#read_committed_snapshot),
a setting of the whole database, and file content makes the database large, which affects how you back it up.

Keep the connection string out of source control, in user secrets, environment variables or a secrets store. Anything
`Microsoft.Data.SqlClient` accepts works.

## Provisioning

Provision the database once, before the application uses it, in one of two ways.

**From the application.** `MigrateResourcefullDatabaseAsync` creates the database if it doesn't exist and applies the
migrations it's missing; on an up-to-date database it does nothing:

```csharp
var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
```

Run it from a deployment step, or at startup of a single instance. Turning on `READ_COMMITTED_SNAPSHOT`, which the
first provisioning does, disconnects every other session on the database.

**With the scripts.** `Resourcefull.SqlServer` ships idempotent SQL scripts in its package's `scripts` folder, for
example `~/.nuget/packages/resourcefull.sqlserver/3.0.0/scripts/`. Create the database, then run `Resourcefull.sql`
connected to it:

```shell
sqlcmd -S <server> -d <database> -b -I -i Resourcefull.sql
```

The scripts run against the database you connect to and never create or switch databases. `-b` stops at the first
error, and `-I` turns on `QUOTED_IDENTIFIER`, which the filtered indexes need. `Resourcefull.sql` brings an empty or
older database to the current schema and is safe to run again. The scripts aren't added to projects that reference
the package; they're also in the repository under `src/Resourcefull.SqlServer/Scripts/`.

## Permissions

| Task | Needs |
|---|---|
| Running the application | `db_datareader` and `db_datawriter` on the database |
| Provisioning or upgrading | `db_owner` on the database (to create tables and turn on `READ_COMMITTED_SNAPSHOT`), plus `CREATE DATABASE` if `MigrateResourcefullDatabaseAsync` should create it |
| Recording hashes for older versions | `db_datareader` and `db_datawriter` |

So the application can run with a least-privileged login, and a deployment step with a stronger one provisions the
database.

## READ_COMMITTED_SNAPSHOT

The provider is designed to run with `READ_COMMITTED_SNAPSHOT` (RCSI) on, and provisioning turns it on. With RCSI,
reads use row versions instead of shared locks, which removes a class of reader/writer deadlocks under concurrent load.
The row versions live in `tempdb`.

RCSI applies to the whole database. In a database shared with your application it changes how your own queries read
too: they see the last committed data instead of waiting for writers. Most applications are unaffected or better off,
but if yours relies on read-committed blocking, give Resourcefull a database of its own.

## Retries

`UseSqlServer` turns on EF Core's retry policy for SQL Server's transient errors, including deadlocks (error 1205): up
to 5 retries, at most 10 seconds apart. It stops once the operation's cancellation token is cancelled.

An upload is retried as a whole, from its first byte. That's possible only when its stream can seek; an upload from a
stream that can't, such as an HTTP request body, fails instead once its content has started, and stores nothing.

## How content is stored

Content is stored in chunks of up to 64 KB. An upload inserts them in one transaction, so a version isn't visible until
all of its content is stored, and the name is locked only at the end, to make the new version current. A download
reads a few chunks at a time as its stream is read, so memory use stays flat whatever the file size.

## Performance

- **Memory doesn't grow with file size.** An upload or a download holds about 1 MiB of content at a time (16 chunks),
  in buffers it reuses, whatever the file's size. Eight uploads at once hold about 8 MiB of content between them.
- **About one round trip per MiB.** An upload inserts 16 chunks per round trip and a download reads 16 per query, plus
  a few round trips per operation to find the version, lock the name and make the version current. Reading a
  resource's attributes, listing a container and listing a name's versions are one query each.
- **Throughput is the server's.** With the client and SQL Server on the same machine, uploads of large files run at
  roughly 60 to 120 MiB/s and downloads at 150 to 375 MiB/s. On a network, latency and bandwidth set the pace, and
  the round trips above are what latency multiplies.
- **The transaction log takes about as much as the content.** An upload writes about 1.13 times its size to the log,
  and deleting a version about 1.07 times its size, since SQL Server logs the content it frees. Size the log for the
  largest uploads and deletes you expect to run at once, and back it up regularly under the full recovery model.
- **Concurrent writers don't wait for each other's content.** Uploads to different names run fully in parallel, and
  uploads to the same name wait for each other only while making their versions current.

These figures come from the performance tests the repository runs on every change (see
[CONTRIBUTING.md](../../CONTRIBUTING.md#performance-tests)), which fail the build when an operation gets more expensive.
There's nothing to tune: the chunk and page sizes are fixed.

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
