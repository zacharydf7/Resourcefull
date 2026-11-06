# PostgreSQL

`Resourcefull.PostgreSql` stores resources in a PostgreSQL database, splitting content into chunks so files of any
size stream in and out with flat memory use. It's tested against PostgreSQL 16 and 17.

```shell
dotnet add package Resourcefull.PostgreSql
```

```csharp
using Resourcefull;

builder.Services.AddResourcefull(options => options
    .UsePostgreSql(builder.Configuration.GetConnectionString("Resources")!));
```

It does everything the [SQL Server provider](sql-server.md) does, including versions, conditional writes, copies and
moves, expiry, container policies, and export and import, and it passes the same contract tests. This page covers what's
particular to PostgreSQL; [Provider features](features.md) compares the providers.

## Choosing the database

The connection string names the database that holds Resourcefull's tables: `ResourceContainers`, `Resources`,
`ResourceChunks` and `ResourceMetadata`, plus EF Core's migration history, `__EFMigrationsHistory`. It can be a database
of its own or your application's, as long as none of your tables share those names, or Resourcefull's tables are in a
[schema of their own](#choosing-a-schema). Anything Npgsql accepts in a connection string works; keep it out of source
control, in user secrets, environment variables or a secrets store.

## Choosing a schema

Without a schema, Resourcefull's tables go where the connection's search path puts them, normally `public`, beside your
own. To keep them apart, give `UsePostgreSql` a schema:

```csharp
builder.Services.AddResourcefull(options => options
    .UsePostgreSql(connectionString, o => o.Schema = "resourcefull"));
```

Resourcefull then qualifies its tables' names with that schema, as in `resourcefull."Resources"`, so they go there.
So does its migration history, `resourcefull."__EFMigrationsHistory"`, which it then no longer shares with your own
EF Core migrations. `MigrateResourcefullDatabaseAsync` creates the schema if it's missing. Because the names
are qualified rather than found through the search path, this works behind a connection pooler such as PgBouncer too.

A schema's name is a letter or underscore, then up to 62 letters, digits or underscores. Every instance of the
application must use the same schema: an instance configured without it would look for the tables in `public`.

## Provisioning

Provision the database once, before the application uses it, in one of two ways.

**From the application.** `MigrateResourcefullDatabaseAsync` creates the database if it doesn't exist and applies the
migrations it's missing; on an up-to-date database it does nothing:

```csharp
var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
```

Run it from a deployment step, or at the startup of a single instance.

**With a script.** For a database administrator to run, have the application write the script, using the configuration
it runs with:

```csharp
File.WriteAllText("resourcefull.sql", app.Services.GenerateResourcefullScript());
```

Create the database, then run the script connected to it:

```shell
psql "host=<server> dbname=<database>" -v ON_ERROR_STOP=1 -f resourcefull.sql
```

`-v ON_ERROR_STOP=1` stops at the first error. The script applies only the migrations the database is missing, so
it's safe to run again. With a schema, every table name in it is qualified with that schema.

## Permissions

| Task | Needs |
|---|---|
| Running the application | `SELECT`, `INSERT`, `UPDATE` and `DELETE` on Resourcefull's tables, and `USAGE` on their schema |
| Provisioning or upgrading | Ownership of the tables or `CREATE` on their schema; `CREATE` on the database, if it has to create the schema; and `CREATEDB`, if `MigrateResourcefullDatabaseAsync` should create the database |

So the application can run with a least-privileged role, and a deployment step with a stronger one provisions the
database.

## How it differs from SQL Server

- **Times are kept to the microsecond,** PostgreSQL's precision. An expiry, or an imported version's `LastModified`,
  given more precisely is stored with the rest cut off: a write returns it as given, and later reads return it cut.
  The times Resourcefull sets itself, such as when a version was stored, are whole microseconds already, so they read
  back exactly as the write returned them.
- **Listings order names by Unicode code point,** PostgreSQL's byte order. It matches ordinal order (`StringComparer.Ordinal`)
  except between characters beyond U+FFFF, such as emoji, and those from U+E000 to U+FFFF: PostgreSQL lists `😀`
  (U+1F600) after `�` (U+FFFD), where ordinal order lists it before. A listing still returns every name exactly once. See
  [Listing resources](../listing.md).
- **Text can't hold U+0000,** the null character, in PostgreSQL. No stored name or version ID has one, so a lookup of
  one finds nothing. Storing a version whose name, version ID, content type or origin has one, such as a name from an
  older release's export, throws `ArgumentException`, as does setting such a content type. A copy or move to a
  container whose name has one reports that the container doesn't exist.
- **A write locks its name with an advisory lock,** `pg_advisory_xact_lock`, which ends with its transaction, so
  writers of a name take turns. The lock's key is a 64-bit hash of the name, in the same key space as any advisory
  locks your application takes; a collision only makes one wait for the other.

## Retries

`UsePostgreSql` turns on EF Core's retry policy for PostgreSQL's transient errors, including deadlocks (`40P01`) and
serialization failures (`40001`): up to 5 retries, at most 10 seconds apart. Change it, or the command timeout, with
`ConfigurePostgreSql`:

```csharp
builder.Services.AddResourcefull(options => options
    .UsePostgreSql(connectionString, o => o.ConfigurePostgreSql = npgsql => npgsql.CommandTimeout(60)));
```

Replacing the retry policy, such as with `EnableRetryOnFailure`, also stops Resourcefull from counting retries in
`resourcefull.postgresql.retries`.

## Health checks

```csharp
builder.Services.AddHealthChecks().AddResourcefullPostgreSqlCheck(tags: ["ready"]);
```

It reports whether the database can be reached and has every migration this release needs, as the
[SQL Server check](../health-checks.md) does, with `postgresql` as the provider in its data.

## Telemetry

Spans and metrics carry `resourcefull.provider` `postgresql`. The provider's own measurements are
`resourcefull.postgresql.retries`, with the SQLSTATE code in `db.response.status_code`, and
`resourcefull.postgresql.abandoned_uploads`, uploads that stopped partway and that the [expiry](../expiry.md) sweep
deleted. See [Telemetry](../telemetry.md).
