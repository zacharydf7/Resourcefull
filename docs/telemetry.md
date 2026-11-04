# Telemetry

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

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
| `resourcefull.client.uploaded` | counter | By | content bytes stored by uploads, restores and imports, and by completed [upload sessions](uploading-in-parts.md) |
| `resourcefull.client.appended` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | counter | By | content bytes stored by appends to upload sessions, before the sessions complete |
| `resourcefull.client.downloaded` | counter | By | content bytes read from download streams, counted as they're read |
| `resourcefull.client.inspection.duration` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | histogram | s | how long your [upload inspector](upload-inspection.md) took to answer, from the start of the upload or imported version, with `resourcefull.inspection_outcome`: `accepted`, `refused`, `failed` or `canceled` |

Every measurement in this table carries these tags:

- **`resourcefull.operation`:** one of the operations below.
- **`resourcefull.provider`:** `sqlserver`, `postgresql`, `s3`, `azureblob`, `inmemory`, or a custom provider's type name.
- **`resourcefull.store`** <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>**:** the store's name, for a store added with `AddKeyedResourcefull`; see
  [Several stores](several-stores.md). The store `AddResourcefull` adds records none.
- **`error.type`:** the exception's type, on a failed operation only.

Container and resource names aren't metric tags: there can be any number of them, which would swamp a metrics backend.

### Work that happens out of sight

Some of Resourcefull's work doesn't fail the call that started it, so it would go unnoticed without a counter of its
own. Each carries `resourcefull.provider`. The five that count a failure in Resourcefull's clients (`prune.failures`,
`expire.failures`, `purge.failures`, `policy.last_known_uses` and `cleanup_handler.failures`) and the SQL Server and
PostgreSQL `retries` also carry `error.type`:

| Instrument | Unit | What it counts |
|---|---|---|
| `resourcefull.client.prune.failures` | {failure} | prunes of older versions that failed after the upload, restore or copy was stored, or for a resource `PruneContainerAsync` couldn't prune; the older versions stay until a later prune |
| `resourcefull.client.expired` | {version} | expired versions that [expiry](expiry.md) sweeps deleted |
| `resourcefull.client.expire.failures` | {version} | expired versions a sweep failed to delete; they stay for the next sweep |
| `resourcefull.client.purged` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {version} | versions purged from a [bin](deleting-safely.md#the-bin) for good, by sweeps and by `PurgeDeletedAsync` |
| `resourcefull.client.purge.failures` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {version} | versions due to leave a bin that a sweep failed to purge; they stay for the next sweep |
| `resourcefull.client.undeleted` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {version} | versions `UndeleteAsync` brought back from a bin |
| `resourcefull.client.copy.versions` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {version} | versions [copies to another store](moving-providers.md) imported, changed or deleted there, with `resourcefull.copy_action`: `imported`, `changed` or `deleted`. The bytes a copy stores are counted by the destination, as an import's, in `resourcefull.client.uploaded` |
| `resourcefull.client.policy.last_known_uses` | {use} | times a container's last known [policy](container-policies.md) was used because the policy provider failed, at most once per container each `cacheFor` |
| `resourcefull.client.delete.refused` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {delete} | deletes, container deletes and prunes refused because a version they'd delete is [retained or under a legal hold](deleting-safely.md), with `resourcefull.operation` (`delete`, `delete_container` or `prune`) and `resourcefull.refusal_reason`: `retain_until` or `legal_hold`. A prune is counted only when a hold or date was set after it read the versions; it skips versions it already knows are protected |
| `resourcefull.client.metadata.indexed` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {version} | versions `IndexMetadataAsync` indexed for [searching by metadata](listing.md#finding-resources-by-metadata) |
| `resourcefull.client.cleanup_handler.failures` | {failure} | calls to your [cleanup handler](expiry.md#auditing-whats-deleted) that threw, with `resourcefull.cleanup_reason`: `expired`, `pruned` or `purged`. A handler failure also counts as a failed prune, expired version or purge, since the version stays |
| `resourcefull.sqlserver.abandoned_uploads` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {upload} | staged versions of uploads that stopped partway, which the [expiry](expiry.md) sweep deleted a day after they last stored anything; upload sessions nothing was added to are counted here too |
| `resourcefull.sqlserver.retries` | {retry} | SQL Server calls retried after a transient failure, such as a deadlock or a dropped connection, with `db.response.status_code`: the SQL Server error number, such as `1205` for a deadlock |
| `resourcefull.postgresql.abandoned_uploads` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {upload} | as `resourcefull.sqlserver.abandoned_uploads`, for [PostgreSQL](providers/postgresql.md) |
| `resourcefull.postgresql.retries` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {retry} | PostgreSQL calls retried after a transient failure, with `db.response.status_code`: the SQLSTATE code, such as `40P01` for a deadlock |
| `resourcefull.sqlite.abandoned_uploads` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {upload} | as `resourcefull.sqlserver.abandoned_uploads`, for [SQLite](providers/sqlite.md) |
| `resourcefull.s3.retries` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {retry} | writes to a name's head, or to an upload session's record, retried because another writer changed it first; see [S3-compatible storage](providers/s3.md#telemetry) |
| `resourcefull.azureblob.retries` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {retry} | the same, with Azure Blob Storage; see [Azure Blob Storage](providers/azure-blob.md#telemetry) |
| `resourcefull.filesystem.retries` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {retry} | the same, with the [file system](providers/file-system.md#telemetry) |
| `resourcefull.s3.abandoned_uploads` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {upload} | [upload sessions](uploading-in-parts.md) nothing was appended to for a day, or whose container was deleted, and content no head names, which the [expiry](expiry.md) sweep deleted |
| `resourcefull.azureblob.abandoned_uploads` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {upload} | the same, with Azure Blob Storage |
| `resourcefull.filesystem.abandoned_uploads` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {upload} | the same, with the [file system](providers/file-system.md#telemetry) |
| `resourcefull.s3.moves_recovered` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {move} | moves the [expiry](expiry.md) sweep finished or undid because their mover stopped first; see [S3-compatible storage](providers/s3.md#telemetry) |
| `resourcefull.azureblob.moves_recovered` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {move} | the same, with Azure Blob Storage |
| `resourcefull.filesystem.moves_recovered` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {move} | the same, with the [file system](providers/file-system.md#telemetry) |
| `resourcefull.s3.session_joins` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {join} | joins of an [upload session](uploading-in-parts.md)'s segments, made once they took as many parts as the store joins; each delays an append while the store copies them; see [S3-compatible storage](providers/s3.md#telemetry) |
| `resourcefull.azureblob.session_joins` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {join} | the same, with Azure Blob Storage |
| `resourcefull.filesystem.session_joins` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {join} | the same, with the [file system](providers/file-system.md#telemetry), where it stays at zero |
| `resourcefull.s3.stale_search_entries`, `resourcefull.azureblob.stale_search_entries`, `resourcefull.filesystem.stale_search_entries` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {entry} | metadata search entries a search dropped because their name's current version no longer had the value; see [Finding resources by metadata](listing.md#finding-resources-by-metadata) |
| `resourcefull.s3.stale_expiries`, `resourcefull.azureblob.stale_expiries`, `resourcefull.filesystem.stale_expiries` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {entry} | expiry entries a sweep dropped because their version no longer existed or had another expiry; see [S3-compatible storage](providers/s3.md#how-it-stores-things) or [Azure Blob Storage](providers/azure-blob.md#how-it-stores-things) |
| `resourcefull.s3.stale_bin_entries`, `resourcefull.azureblob.stale_bin_entries`, `resourcefull.filesystem.stale_bin_entries` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | {entry} | bin and purge entries a listing of the bin, a sweep or the daily scan for unnamed content dropped because their version was no longer in its [bin](deleting-safely.md#the-bin) |

What to alert on:

- **Any prune, expire or cleanup handler failure** that keeps happening. A single one is retried by the next upload or
  sweep; a steady count means storage is growing, or expired files are staying, until you fix the cause.
- **A jump in retries.** A few are normal on a busy database. Many at once mean the database is struggling, and each
  retry makes the operation slower; the error numbers say whether it's deadlocks, timeouts or connections.
- **Failed inspections,** with `resourcefull.inspection_outcome` `failed`. Your upload inspector is throwing, such as
  when the malware scanner is down, and every upload it inspects fails.
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
| `upload` | `UploadAsync`, both overloads | `resourcefull.conditional`; `resourcefull.write_mode` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>: `create` or `replace`; a `resourcefull prune` child span for pruning older versions; with an [upload inspector](upload-inspection.md) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, a `resourcefull inspect` child span, with `resourcefull.inspection_outcome`; with SQL Server <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, `resourcefull.batches`: the batches of about a MiB its content was stored in |
| `upload_start` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `StartUploadAsync`, which starts an [upload session](uploading-in-parts.md) | |
| `upload_get` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `GetUploadAsync` | |
| `upload_append` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `AppendUploadAsync` | `resourcefull.offset`: where the append started |
| `upload_complete` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `CompleteUploadAsync` | `resourcefull.conditional`; `resourcefull.write_mode`: `create` or `replace`; a `resourcefull prune` child span; with an [upload inspector](upload-inspection.md), a `resourcefull inspect` child span |
| `upload_cancel` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `CancelUploadAsync` | |
| `restore` | `RestoreVersionAsync` | a `resourcefull prune` child span; with SQL Server <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, `resourcefull.batches` when it stores the version's content again, as for a version stored before 3.0 without a hash |
| `download` | `DownloadStreamingAsync`, `DownloadContentAsync` | for a range, `resourcefull.range`, as HTTP writes it: `100-199`, or `100-` to the end |
| `get_info` | `GetResourceInfoAsync` | |
| `exists` | `ExistsAsync` | |
| `get_usage` | `GetUsageAsync` | |
| `get_container_policy` | `GetPolicyAsync` | |
| `copy` | `CopyResourceAsync` | `resourcefull.destination_container`; `resourcefull.write_mode` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>: `create` or `replace`; a `resourcefull prune` child span; with SQL Server <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, `resourcefull.batches` when it stores the content again, as for a version stored before 3.0 without a hash |
| `move` | `MoveResourceAsync`, `RenameResourceAsync` | `resourcefull.destination_container` |
| `set_expiry` | `SetExpiryAsync` | |
| `set_metadata` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `SetResourceMetadataAsync` | |
| `set_retention` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `SetRetentionAsync` | |
| `set_legal_hold` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `SetLegalHoldAsync` | `resourcefull.legal_hold`: whether it set or lifted the hold |
| `set_content_type` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `SetContentTypeAsync` | |
| `set_container_metadata` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `SetContainerMetadataAsync` | |
| `export` | `ExportContainerAsync` | `resourcefull.resources`, `resourcefull.versions`: what it wrote; `resourcefull.pages`: the pages of 1,000 names it read |
| `import` | `ImportContainerAsync`, from an export or <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> from a stream of versions | `resourcefull.resources`, `resourcefull.versions`: what it stored; with an [upload inspector](upload-inspection.md) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, a `resourcefull inspect` child span for each version; with SQL Server <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, `resourcefull.batches`: the batches of about a MiB every version's content was stored in |
| `import_versions` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `ImportVersionsAsync` | `resourcefull.resources`, `resourcefull.versions`: what it stored |
| `copy_container` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `CopyContainerToAsync` | `resourcefull.destination_container`; `resourcefull.destination_store`, when the destination is a store with a name; `resourcefull.resources`, `resourcefull.versions`: what it copied; the destination's `import` as a child span |
| `copy_store` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `CopyStoreToAsync`, which [moves a store](moving-providers.md) | `resourcefull.destination_store`, as for `copy_container`; `resourcefull.copied`, `resourcefull.updated`, `resourcefull.matching`, `resourcefull.failed`: how many containers it copied, brought up to date, found matching and failed |
| `prune_container` | `PruneContainerAsync` | `resourcefull.pruned`: how many versions were past the limit |
| `expire` | `DeleteExpiredAsync`, and each sweep of [`UseExpiry`](expiry.md), when it finds expired versions, read-only containers' included | `resourcefull.expired`: how many versions it deleted; `resourcefull.batches`: how many batches of up to 100 it deleted them in; `resourcefull.batch_fallbacks`, when a batch failed and its versions were deleted one at a time instead: how many. Each failed batch's exception is recorded too, as on a failed operation, but the span isn't marked failed: the sweep carried on |
| `list_page` | `GetResourcePageAsync`, and each page `ListResourcesAsync` fetches | `resourcefull.page_size`; for a [search by metadata](listing.md#finding-resources-by-metadata) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, also `resourcefull.metadata_key`: the key searched, never its value; and with the file system, S3 and Azure Blob, `resourcefull.search_examined`: how many index entries the page read |
| `index_metadata` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `IndexMetadataAsync` | `resourcefull.indexed`: how many versions it indexed |
| `list_versions` | `GetResourceVersionsAsync` | |
| `list_top` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `GetTopVersionsAsync` | `resourcefull.order`: `largest_first` or `expiring_first` |
| `list_expired` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `GetExpiredVersionsAsync` | |
| `list_deleted` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `GetDeletedPageAsync`, and each page of the [bin](deleting-safely.md#the-bin) that `ListDeletedAsync` fetches | `resourcefull.page_size` |
| `undelete` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `UndeleteAsync` | |
| `purge_deleted` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `PurgeDeletedAsync` on a container client | |
| `purge` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | `PurgeDeletedAsync` on the service client, and each sweep of [`UseExpiry`](expiry.md), when it finds versions due to leave a bin | `resourcefull.purged`: how many versions it purged |
| `delete` | `DeleteResourceIfExistsAsync` | `resourcefull.conditional`; when a [retained or held](deleting-safely.md) version refuses it, `resourcefull.refusal_reason` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> |
| `create_container` | `CreateContainerAsync`, `CreateContainerIfNotExistsAsync` | |
| `get_container` | `GetContainerInfoAsync` | |
| `container_exists` | `ContainerExistsAsync` | |
| `list_containers` | `GetContainerPageAsync` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, and each page `ListContainersAsync` fetches | `resourcefull.page_size` |
| `delete_container` | `DeleteContainerIfExistsAsync` | when a [retained or held](deleting-safely.md) version refuses it, `resourcefull.refusal_reason` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> |
| `migrate` | `MigrateResourcefullDatabaseAsync` (SQL Server) | span only; `resourcefull.schema` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>: the [schema](providers/sql-server.md#choosing-a-schema) given to `UseSqlServer`, when there is one |
| `backfill` | `ContentHashBackfill.RunAsync` (SQL Server) | span only; `resourcefull.hashed`, `resourcefull.mismatches` |
| `health_check` | `AddResourcefullCheck`; `AddResourcefullSqlServerCheck` (span only) | for SQL Server, `resourcefull.pending_migrations` |

`GetContainerClient` doesn't contact storage, so it records nothing. With
[container policies](container-policies.md), every operation that checks one (`upload`, `restore`, `copy`, `move`,
`delete`, `delete_container`, `prune_container`, `set_metadata`, `set_content_type`, `set_retention`,
`set_legal_hold`, `set_container_metadata`, `undelete`, `purge_deleted` and `get_container_policy`) records a `resourcefull get_policy` child span when it asks the policy provider, and
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
name, so its 416 shows up as a failed `download` with `error.type` `Resourcefull.ResourceRangeNotSatisfiableException`;
filter that type out of storage error rates. ASP.NET
Core records each request's status code, 304, 412 and 413 included, in its own metrics and spans. When a request has
preconditions, the package adds `resourcefull.precondition` to the request's span: `met`, `not_modified` or `failed`.
A request that opens a [signed link](sharing-links.md), or uploads through an [upload link](upload-links.md), gets
`resourcefull.link`: `valid`, `expired` or `invalid`. A link for a [named store](several-stores.md) also gets
`resourcefull.store`. A request to the [dashboard](aspnet-core/dashboard.md) gets `resourcefull.dashboard`, naming
what it asked for, such as `containers`, `largest` or, for a change, `upload`; the
[dashboard's telemetry](aspnet-core/dashboard.md#telemetry) lists them all.
