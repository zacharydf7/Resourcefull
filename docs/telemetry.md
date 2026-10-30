# Telemetry

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

Resourcefull publishes a trace span for every operation, and metrics for durations, bytes and work that happens out
of sight, through .NET's `ActivitySource` and `Meter`. Nothing is measured until something subscribes; until then, an
operation does little more than check whether anything is listening.

With OpenTelemetry, subscribe to both names:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(ResourcefullTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(ResourcefullTelemetry.MeterName));
```

Both names are `Resourcefull`. With SQL Server, SqlClient's own instrumentation, when you enable it, adds each
database call as a child of the operation's span. With .NET Aspire, add them to the service defaults; see
[Aspire](aspire.md).

## Metrics

| Instrument | Type | Unit | What it measures |
|---|---|---|---|
| `resourcefull.client.operation.duration` | histogram | s | each operation, from call to result |
| `resourcefull.client.uploaded` | counter | By | content bytes stored by uploads, restores and imports |
| `resourcefull.client.downloaded` | counter | By | content bytes read from download streams, counted as they're read |

Every measurement in this table carries these tags:

- **`resourcefull.operation`:** one of the operations below.
- **`resourcefull.provider`:** `sqlserver`, `inmemory`, or a custom provider's type name.
- **`error.type`:** the exception's type, on a failed operation only.

Container and resource names aren't metric tags: there can be any number of them, which would swamp a metrics backend.

### Work that happens out of sight

Some of Resourcefull's work doesn't fail the call that started it, so it would go unnoticed without a counter of its
own. Each carries `resourcefull.provider`, and all but `resourcefull.client.expired` carry `error.type`:

| Instrument | Unit | What it counts |
|---|---|---|
| `resourcefull.client.prune.failures` | {failure} | prunes of older versions that failed after the upload, restore or copy was stored; the older versions stay until a later prune |
| `resourcefull.client.expired` | {version} | expired versions that [expiry](expiry.md) sweeps deleted |
| `resourcefull.client.expire.failures` | {version} | expired versions a sweep failed to delete; they stay for the next sweep |
| `resourcefull.client.policy.last_known_uses` | {use} | times a container's last known [policy](container-policies.md) was used because the policy provider failed, at most once per container each `cacheFor` |
| `resourcefull.client.cleanup_handler.failures` | {failure} | calls to your [cleanup handler](expiry.md#auditing-whats-deleted) that threw, with `resourcefull.cleanup_reason`: `expired` or `pruned`. A handler failure also counts as a failed prune or expired version, since the version stays |
| `resourcefull.sqlserver.retries` | {retry} | SQL Server calls retried after a transient failure, such as a deadlock or a dropped connection, with `db.response.status_code`: the SQL Server error number, such as `1205` for a deadlock |

What to alert on:

- **Any prune, expire or cleanup handler failure** that keeps happening. A single one is retried by the next upload or
  sweep; a steady count means storage is growing, or expired files are staying, until you fix the cause.
- **A jump in retries.** A few are normal on a busy database. Many at once mean the database is struggling, and each
  retry makes the operation slower; the error numbers say whether it's deadlocks, timeouts or connections.
- **Any use of a last known policy.** Your policy provider is failing, and writes start failing too once
  `keepLastKnownFor` runs out.

## Spans

Each operation's span is named `resourcefull <operation>`, such as `resourcefull upload`, and has the same tags as its
metrics. It also has:

- **`resourcefull.container`:** the container's name.
- **`resourcefull.resource`:** the resource's name, only after you opt in. Resource names, such as file names, can
  hold personal data:

  ```csharp
  builder.Services.AddResourcefull(options => options
      .UseSqlServer(connectionString)
      .UseTelemetry(telemetry => telemetry.RecordResourceNames = true));
  ```
- **`error.type`, an error status and the exception,** on a failed operation. The exception's message, which
  often names the resource, is recorded only with `RecordResourceNames`; otherwise its type stands in for it.

## Operations

| Operation | Methods | Extra span tags |
|---|---|---|
| `upload` | `UploadAsync`, both overloads | `resourcefull.conditional`; a `resourcefull prune` child span for pruning older versions |
| `restore` | `RestoreVersionAsync` | a `resourcefull prune` child span |
| `download` | `DownloadStreamingAsync`, `DownloadContentAsync` | for a range, `resourcefull.range`, as HTTP writes it: `100-199`, or `100-` to the end |
| `get_info` | `GetResourceInfoAsync` | |
| `exists` | `ExistsAsync` | |
| `get_usage` | `GetUsageAsync` | |
| `copy` | `CopyResourceAsync` | `resourcefull.destination_container`; a `resourcefull prune` child span |
| `move` | `MoveResourceAsync` | `resourcefull.destination_container` |
| `set_expiry` | `SetExpiryAsync` | |
| `export` | `ExportContainerAsync` | `resourcefull.resources`, `resourcefull.versions`: what it wrote |
| `import` | `ImportContainerAsync` | `resourcefull.resources`, `resourcefull.versions`: what it stored |
| `expire` | `DeleteExpiredAsync`, and each sweep of [`UseExpiry`](expiry.md), when it finds expired versions, read-only containers' included | `resourcefull.expired`: how many versions it deleted |
| `list` | `GetResourcesAsync` | |
| `list_page` | `GetResourcePageAsync`, and each page `ListResourcesAsync` fetches | `resourcefull.page_size` |
| `list_versions` | `GetResourceVersionsAsync` | |
| `delete` | `DeleteResourceIfExistsAsync` | `resourcefull.conditional` |
| `create_container` | `CreateResourceContainerAsync`, `CreateResourceContainerIfNotExistsAsync` | |
| `get_container` | `GetResourceContainerInfoAsync` | |
| `container_exists` | `ContainerExistsAsync` | |
| `list_containers` | `GetResourceContainersAsync` | |
| `delete_container` | `DeleteResourceContainerIfExistsAsync` | |
| `migrate` | `MigrateResourcefullDatabaseAsync` (SQL Server) | span only |
| `backfill` | `ContentHashBackfill.RunAsync` (SQL Server) | span only; `resourcefull.hashed`, `resourcefull.mismatches` |
| `health_check` | `AddResourcefullCheck`; `AddResourcefullSqlServerCheck` (span only) | for SQL Server, `resourcefull.pending_migrations` |

`GetResourceContainerClient` doesn't contact storage, so it records nothing. With
[container policies](container-policies.md), every operation that checks one (`upload`, `restore`, `copy`, `move`,
`delete` and `delete_container`) records a `resourcefull get_policy` child span when it asks the policy provider, and
`resourcefull.policy_rule` when the policy refuses it. When the provider fails and the container's last known policy is
used instead, the `get_policy` span records the failure and `resourcefull.policy_last_known`.

## Logs

Resourcefull logs through `ILogger`, under its own class names in the `Resourcefull` namespace: a warning when a prune
fails, and an error when an expiry sweep fails. With SQL Server, EF Core also logs under its own categories, which can
look alarming:

- **`Microsoft.EntityFrameworkCore.Infrastructure`** logs each retry of a transient failure, at `Information`. The
  `resourcefull.sqlserver.retries` counter counts the same retries.
- **`Microsoft.EntityFrameworkCore.Database.Command`** and **`Microsoft.EntityFrameworkCore.Update`** log every database
  command that fails, at `Error`, including failures Resourcefull expects and turns into its own exceptions. A write to
  a container that doesn't exist fails with a foreign key error (547) and reaches your code as
  `ResourceContainerNotFoundException`; two callers creating the same container at once fail with a duplicate key error
  (2627 or 2601) and reach your code as `ResourceConflictException`.

Your code sees each failure as an exception either way, so if those error logs are noise for you, raise the two
categories' level in `appsettings.json`. This hides EF Core's logs of unexpected failures too, so do it only where your
application logs the exceptions it catches:

```json
{
  "Logging": {
    "LogLevel": {
      "Microsoft.EntityFrameworkCore.Database.Command": "Critical",
      "Microsoft.EntityFrameworkCore.Update": "Critical"
    }
  }
}
```

## ASP.NET Core

`Results.Resource` and `UploadFromRequestAsync` call the operations above, so they're covered through them; a range
request's download carries `resourcefull.range`. A range that starts past the end is invalid input, like an invalid
name, so its 416 shows up as a failed `download` with `error.type` `System.ArgumentOutOfRangeException`; filter that
type out of storage error rates. ASP.NET
Core records each request's status code, 304, 412 and 413 included, in its own metrics and spans. When a request has
preconditions, the package adds `resourcefull.precondition` to the request's span: `met`, `not_modified` or `failed`.
A request that opens a [signed link](sharing-links.md) gets `resourcefull.link`: `valid`, `expired` or `invalid`.
