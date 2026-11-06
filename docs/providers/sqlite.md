# SQLite

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

`Resourcefull.Sqlite` stores resources in a SQLite database: a single file, with no database server to run. Content is
split into chunks, so files of any size stream in and out with flat memory use, and every name keeps its versions,
content hashes and metadata, as with the other providers. It suits desktop and edge applications, a single server, and
tests that want a real database without a server.

```shell
dotnet add package Resourcefull.Sqlite
```

```csharp
using Resourcefull;

builder.Services.AddResourcefull(options => options.UseSqlite("Data Source=files.db"));

var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
```

It does everything the other providers do, including versions, conditional writes, copies and moves, expiry, upload
sessions, container policies, and export and import, and it passes the same contract tests. This page covers what's
particular to SQLite; [Provider features](features.md) compares the providers.

## Choosing the database

The connection string names the database file, such as `Data Source=files.db`; a relative path is taken from the current
directory. The file holds Resourcefull's tables, `ResourceContainers`, `Resources`, `ResourceChunks` and
`ResourceMetadata`, plus EF Core's migration history, `__EFMigrationsHistory`. It can be a file of its own or your
application's, as long as none of your tables share those names. Anything Microsoft.Data.Sqlite accepts in a connection
string works.

Keep the file on a local disk, used from one machine. SQLite relies on the filesystem's locks to keep writers apart,
which network filesystems don't always honor, and write-ahead logging, which provisioning turns on, shares its index
through memory on one machine, so sharing the file between machines can corrupt it. For several servers, use
[SQL Server](sql-server.md), [PostgreSQL](postgresql.md) or the [file system](file-system.md) on a Windows file share.

The account the application runs as needs to read and write the file and to create files beside it: with write-ahead
logging, SQLite keeps two more, named after the database with `-wal` and `-shm` on the end.

## Provisioning

`MigrateResourcefullDatabaseAsync` creates the file if it doesn't exist, turns on write-ahead logging, and applies the
migrations it's missing; on an up-to-date database it does nothing. Run it at startup, or from a deployment step.

`GenerateResourcefullScript` writes the migrations as SQL, every one from the first: for SQLite, EF Core can't write a
script that skips the ones a database already has. So apply the script only to a new database, and turn on write-ahead
logging yourself first, since a script can't do that inside its transaction:

```shell
sqlite3 files.db "PRAGMA journal_mode=WAL;"
sqlite3 files.db < resourcefull.sql
```

## One writer at a time

SQLite lets one connection write at a time. Each of Resourcefull's writes is a short transaction that takes the
database's write lock as it begins, so writes take turns, whether they're to the same name or to different ones, and a
write never sees another half done. A write that finds the lock taken waits for it, for up to the command timeout, 30
seconds by default, then fails. With write-ahead logging, reads don't wait for writes, nor writes for reads.

An upload holds the lock only for each batch of about 1 MiB of its content, and to make its version current at the end,
so a slow upload doesn't hold up the others. Many fast writes at once still take turns: if your application writes
heavily from many threads, or from several processes, a server database will keep up better.

Copying a version, or deleting a large one, holds the lock until it's done, which for files of many gigabytes can take
long enough for other writes to time out. If you keep files that large, raise the timeout with `ConfigureSqlite`:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlite("Data Source=files.db", o => o.ConfigureSqlite = sqlite => sqlite.CommandTimeout(60)));
```

Unlike the SQL Server and PostgreSQL providers, this one doesn't retry failed calls, since there's no server connection
to drop.

## Backups

Copying the file while the application writes can catch it partway through a write, and with write-ahead logging, the
latest writes may still be in the `-wal` file beside it. Stop the application before copying the file, and copy any
`-wal` file left with it, or take a consistent copy while it runs with SQLite's own backup, which reads it all at one
moment, `-wal` included. The `sqlite3` command-line shell, a separate download from sqlite.org, does that:

```shell
sqlite3 files.db ".backup files-backup.db"
```

[Export](../backup-and-move.md) moves or archives a container whatever the provider.

## How it differs from the other providers

- **Times are stored as numbers,** their UTC ticks, since SQLite has no type for times. Ticks order and compare as the
  instants do, and they keep .NET's full precision, a tenth of a microsecond, as SQL Server does and PostgreSQL doesn't.
- **Listings order names by Unicode code point,** SQLite's `BINARY` collation, as with PostgreSQL. It matches ordinal
  order (`StringComparer.Ordinal`) except between characters beyond U+FFFF, such as emoji, and those from U+E000 to
  U+FFFF. A listing still returns every name exactly once. See [Listing resources](../listing.md).
- **Text can't hold U+0000,** the null character, in practice: SQLite's text functions, such as the ones a prefix is
  matched with, stop at it. So, as with PostgreSQL, a lookup of a name or version ID with one finds nothing, and storing
  a version whose name, version ID, content type or origin has one throws `ArgumentException`.
- **Deleting doesn't shrink the file.** SQLite reuses the space for what's stored next. To give it back to the disk, run
  `VACUUM` while nothing writes, which rewrites the whole file.

## Health checks

```csharp
builder.Services.AddHealthChecks().AddResourcefullSqliteCheck(tags: ["ready"]);
```

It reports whether the database can be opened and has every migration this release needs, as the
[SQL Server check](../health-checks.md) does, with `sqlite` as the provider in its data. Opening a file that doesn't
exist creates it empty, which the check reports as missing every migration.

## Telemetry

Spans and metrics carry `resourcefull.provider` `sqlite`. The provider's own measurement is
`resourcefull.sqlite.abandoned_uploads`: uploads that stopped partway and that the [expiry](../expiry.md) sweep deleted.
See [Telemetry](../telemetry.md).
