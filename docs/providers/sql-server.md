# SQL Server

`Resourcefull.SqlServer` stores resources in a SQL Server database, splitting content into chunks so files of any size
stream in and out with flat memory use. It's tested against SQL Server 2022 and LocalDB.

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!));
```

## Choosing the database

The connection string names the database that holds Resourcefull's tables: `ResourceContainers`, `Resources` and
`ResourceChunks`, plus EF Core's migration history, `__EFMigrationsHistory`. It can be a database of its own or your
application's, as long as none of your tables share the first three names. The history table can be shared with your
own EF Core migrations.

A database of its own is the better default. Provisioning turns on [`READ_COMMITTED_SNAPSHOT`](#read_committed_snapshot),
a setting of the whole database, and file content makes the database large, which affects how you back it up (see
[Backups and recovery](../backups-and-recovery.md)).

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
example `~/.nuget/packages/resourcefull.sqlserver/<version>/scripts/`. Create the database, then run `Resourcefull.sql`
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
to 5 retries, at most 10 seconds apart. It stops once the operation's cancellation token is canceled. Each retry is
counted in the `resourcefull.sqlserver.retries` metric, with the error number; see [Telemetry](../telemetry.md).

To change these or other connection settings <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, pass a second argument, which gets EF Core's SQL Server
options after Resourcefull has set its own. For example, to give slow statements, such as copying a large file, longer
than the default 30 seconds:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString, sql => sql.CommandTimeout(120)));
```

Replacing the retry policy, such as with `sql.EnableRetryOnFailure(...)`, works too, but then Resourcefull no longer
counts retries in `resourcefull.sqlserver.retries`.

An upload is retried as a whole, from its first byte. That's possible only when its stream can seek; an upload from a
stream that can't, such as an HTTP request body, fails instead once its content has started, and stores nothing.
Copies, restores and imports <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> are retried too: copies and restores run within the database, and an import
reads the version's file from the export again. The exception is copying or restoring a version stored before 3.0
that has no hash yet, which reads it through the application; [record the hashes](sql-server-upgrades.md#recording-hashes-for-older-versions) to retry those too.

Once it's running, see [How SQL Server stores files](sql-server-storage.md) for what to expect from it, and
[Upgrading the database](sql-server-upgrades.md) for moving to a new release.
