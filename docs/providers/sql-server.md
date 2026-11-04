# SQL Server

`Resourcefull.SqlServer` stores resources in a SQL Server database, splitting content into chunks so files of any size
stream in and out with flat memory use. It's tested against SQL Server 2019 and 2022 on every change, and LocalDB in
development. It supports every feature; [Provider features](features.md) compares the providers.

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!));
```

## Choosing the database

The connection string names the database that holds Resourcefull's tables: `ResourceContainers`, `Resources`,
`ResourceChunks` and `ResourceMetadata`, plus EF Core's migration history, `__EFMigrationsHistory`. It can be a database
of its own or your application's, as long as none of your tables share the first four names, or Resourcefull's tables
are in a [schema of their own](#choosing-a-schema). Without a schema, the history table can be shared with your own EF
Core migrations.

A database of its own is the better default. Provisioning turns on [`READ_COMMITTED_SNAPSHOT`](#read_committed_snapshot),
a setting of the whole database, and file content makes the database large, which affects how you back it up (see
[Backups and recovery](../backups-and-recovery.md)). Your application's database has one advantage: a single backup
holds your rows and the files they point at, as they were at the same moment. Either way, Resourcefull's writes don't
join your transactions; [Files and your own data](../files-and-your-data.md) shows how to keep the two in step.

Keep the connection string out of source control, in user secrets, environment variables or a secrets store. Anything
`Microsoft.Data.SqlClient` accepts works.

## Choosing a schema

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Without a schema, Resourcefull's tables go in the database user's default schema, normally `dbo`. In your
application's database that puts them beside your own tables. To keep them apart, give `UseSqlServer` a schema:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString, sql => { }, schema: "resourcefull"));
```

All three tables go in that schema, with their indexes and keys, and every query Resourcefull runs uses it. Resourcefull
also keeps its migration history there, in `resourcefull.__EFMigrationsHistory`, so it no longer shares a history table
with your own EF Core migrations. `MigrateResourcefullDatabaseAsync` creates the schema if it's missing.

A schema's name is a letter or underscore, then up to 127 letters, digits or underscores. Every instance of the
application must use the same schema: an instance configured without it would look for the tables in `dbo` and not
find them.

### Moving existing tables into a schema

A database provisioned without a schema has Resourcefull's tables in `dbo`. To move them into a schema:

1. Stop every instance of the application, and back up the database.
2. Run this connected to the database, as a login with `db_owner`. It moves the tables, with their indexes, keys and
   rows, and copies the migration history into the schema. Copying every row is fine, including your own migrations':
   Resourcefull ignores the ones that aren't its own.

   ```sql
   CREATE SCHEMA [resourcefull];
   GO
   SET XACT_ABORT ON;
   BEGIN TRANSACTION;
   ALTER SCHEMA [resourcefull] TRANSFER [dbo].[ResourceContainers];
   ALTER SCHEMA [resourcefull] TRANSFER [dbo].[Resources];
   ALTER SCHEMA [resourcefull] TRANSFER [dbo].[ResourceChunks];
   ALTER SCHEMA [resourcefull] TRANSFER [dbo].[ResourceMetadata];
   CREATE TABLE [resourcefull].[__EFMigrationsHistory] (
       [MigrationId] nvarchar(150) NOT NULL CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY,
       [ProductVersion] nvarchar(32) NOT NULL);
   INSERT INTO [resourcefull].[__EFMigrationsHistory]
       SELECT [MigrationId], [ProductVersion] FROM [dbo].[__EFMigrationsHistory];
   COMMIT;
   ```

3. Deploy the application with the schema given to `UseSqlServer`, and start it.

Moving a table to another schema doesn't copy its rows, so it's quick whatever their size. Resourcefull's rows stay in
`dbo.__EFMigrationsHistory`, where they do no harm.

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

The packaged scripts are for tables without a [schema](#choosing-a-schema). With a schema, have the application, configured
with that schema, write the script <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> and run it the same way:

```csharp
File.WriteAllText("Resourcefull.sql", app.Services.GenerateResourcefullScript());
```

`GenerateResourcefullScript` builds the script from the same migrations `MigrateResourcefullDatabaseAsync` applies,
without connecting to the database. Like `Resourcefull.sql`, it applies only what the database is missing, so it's
safe to run again.

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
to 5 retries, at most 10 seconds apart. It also retries error 50001, Resourcefull's own: a write waits for a lock on
its resource name, so writers of a name take turns, and SQL Server can end that wait to break a deadlock, or time it
out if you've set a lock timeout. It stops once the operation's cancellation token is canceled. Each retry is
counted in the `resourcefull.sqlserver.retries` metric, with the error number; see [Telemetry](../telemetry.md).

To change these or other connection settings <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, pass a second argument, which gets EF Core's SQL Server
options after Resourcefull has set its own. For example, to give slow statements, such as copying a large file, longer
than the default 30 seconds:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString, sql => sql.CommandTimeout(120)));
```

Replacing the retry policy, such as with `sql.EnableRetryOnFailure(...)`, works too, but then Resourcefull no longer
counts retries in `resourcefull.sqlserver.retries`. Give it error 50001 to retry as well, as in
`sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), [50001])`, or a write that loses a deadlock over its name's lock
fails instead of being retried.

An upload <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> is retried a step at a time: its content goes in about a MiB at a time, and a batch that fails is sent
again from what it holds, so even a stream that can't seek, such as an HTTP request body, carries on. Before 4.0 an
upload was retried as a whole, which a stream that can't seek couldn't be.
Copies, restores and imports <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> are retried too: copies and restores run within the database, and an import
reads the version's file from the export again. The exception is copying or restoring a version stored before 3.0
that has no hash yet, which reads it through the application; [record the hashes](sql-server-upgrades.md#recording-hashes-for-older-versions) to retry those too.

Once it's running, see [How SQL Server stores files](sql-server-storage.md) for what to expect from it, and
[Upgrading the database](sql-server-upgrades.md) for moving to a new release.
