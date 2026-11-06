# Telemetry

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
| `resourcefull.client.appended` | counter | By | content bytes stored by appends to upload sessions, before the sessions complete |
| `resourcefull.client.downloaded` | counter | By | content bytes read from download streams, counted as they're read |
| `resourcefull.client.inspection.duration` | histogram | s | how long your [upload inspector](upload-inspection.md) took to answer, from the start of the upload or imported version, with `resourcefull.inspection_outcome`: `accepted`, `refused`, `failed` or `canceled` |

Every measurement in this table carries these tags:

- **`resourcefull.operation`:** one of the operations below.
- **`resourcefull.provider`:** `sqlserver`, `postgresql`, `s3`, `azureblob`, `inmemory`, or a custom provider's type name.
- **`resourcefull.store`:** the store's name, for a store added with `AddKeyedResourcefull`; see
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
| `resourcefull.client.prune.failures` | {failure} | prunes of older versions that failed after the upload, restore or copy was stored, or for a resource `PruneAsync` couldn't prune; the older versions stay until a later prune |
| `resourcefull.client.expired` | {version} | expired versions that [expiry](expiry.md) sweeps deleted |
| `resourcefull.client.expire.failures` | {version} | expired versions a sweep failed to delete; they stay for the next sweep |
| `resourcefull.client.purged` | {version} | versions purged from a [bin](deleting-safely.md#the-bin) for good, by sweeps and by `PurgeDeletedAsync` |
| `resourcefull.client.purge.failures` | {version} | versions due to leave a bin that a sweep failed to purge; they stay for the next sweep |
| `resourcefull.client.undeleted` | {version} | versions `UndeleteAsync` brought back from a bin |
| `resourcefull.client.copy.versions` | {version} | versions [copies to another store](moving-providers.md) imported, changed or deleted there, with `resourcefull.copy_action`: `imported`, `changed` or `deleted`. The bytes a copy stores are counted by the destination, as an import's, in `resourcefull.client.uploaded` |
| `resourcefull.client.policy.last_known_uses` | {use} | times a container's last known [policy](container-policies.md) was used because the policy provider failed, at most once per container each `cacheFor` |
| `resourcefull.client.delete.refused` | {delete} | deletes, container deletes and prunes refused because a version they'd delete is [retained or under a legal hold](deleting-safely.md), with `resourcefull.operation` (`delete`, `delete_container` or `prune`) and `resourcefull.refusal_reason`: `retain_until` or `legal_hold`. A prune is counted only when a hold or date was set after it read the versions; it skips versions it already knows are protected |
| `resourcefull.client.metadata.indexed` | {version} | versions `IndexMetadataAsync` indexed for [searching by metadata](listing.md#finding-resources-by-metadata) |
| `resourcefull.client.cleanup_handler.failures` | {failure} | calls to your [cleanup handler](expiry.md#auditing-whats-deleted) that threw, with `resourcefull.cleanup_reason`: `expired`, `pruned` or `purged`. A handler failure also counts as a failed prune, expired version or purge, since the version stays |
| `resourcefull.sqlserver.abandoned_uploads` | {upload} | staged versions of uploads that stopped partway, which the [expiry](expiry.md) sweep deleted a day after they last stored anything; upload sessions nothing was added to are counted here too |
| `resourcefull.sqlserver.retries` | {retry} | SQL Server calls retried after a transient failure, such as a deadlock or a dropped connection, with `db.response.status_code`: the SQL Server error number, such as `1205` for a deadlock |
| `resourcefull.postgresql.abandoned_uploads` | {upload} | as `resourcefull.sqlserver.abandoned_uploads`, for [PostgreSQL](providers/postgresql.md) |
| `resourcefull.postgresql.retries` | {retry} | PostgreSQL calls retried after a transient failure, with `db.response.status_code`: the SQLSTATE code, such as `40P01` for a deadlock |
| `resourcefull.sqlite.abandoned_uploads` | {upload} | as `resourcefull.sqlserver.abandoned_uploads`, for [SQLite](providers/sqlite.md) |
| `resourcefull.s3.retries` | {retry} | writes to a name's head, or to an upload session's record, retried because another writer changed it first; see [S3-compatible storage](providers/s3.md#telemetry) |
| `resourcefull.azureblob.retries` | {retry} | the same, with Azure Blob Storage; see [Azure Blob Storage](providers/azure-blob.md#telemetry) |
| `resourcefull.filesystem.retries` | {retry} | the same, with the [file system](providers/file-system.md#telemetry) |
| `resourcefull.s3.abandoned_uploads` | {upload} | [upload sessions](uploading-in-parts.md) nothing was appended to for a day, or whose container was deleted, and content no head names, which the [expiry](expiry.md) sweep deleted |
| `resourcefull.azureblob.abandoned_uploads` | {upload} | the same, with Azure Blob Storage |
| `resourcefull.filesystem.abandoned_uploads` | {upload} | the same, with the [file system](providers/file-system.md#telemetry) |
| `resourcefull.s3.moves_recovered` | {move} | moves the [expiry](expiry.md) sweep finished or undid because their mover stopped first; see [S3-compatible storage](providers/s3.md#telemetry) |
| `resourcefull.azureblob.moves_recovered` | {move} | the same, with Azure Blob Storage |
| `resourcefull.filesystem.moves_recovered` | {move} | the same, with the [file system](providers/file-system.md#telemetry) |
| `resourcefull.s3.session_joins` | {join} | joins of an [upload session](uploading-in-parts.md)'s segments, made once they took as many parts as the store joins; each delays an append while the store copies them; see [S3-compatible storage](providers/s3.md#telemetry) |
| `resourcefull.azureblob.session_joins` | {join} | the same, with Azure Blob Storage |
| `resourcefull.filesystem.session_joins` | {join} | the same, with the [file system](providers/file-system.md#telemetry), where it stays at zero |
| `resourcefull.s3.stale_search_entries`, `resourcefull.azureblob.stale_search_entries`, `resourcefull.filesystem.stale_search_entries` | {entry} | metadata search entries a search dropped because their name's current version no longer had the value; see [Finding resources by metadata](listing.md#finding-resources-by-metadata) |
| `resourcefull.s3.stale_expiries`, `resourcefull.azureblob.stale_expiries`, `resourcefull.filesystem.stale_expiries` | {entry} | expiry entries a sweep dropped because their version no longer existed or had another expiry; see [S3-compatible storage](providers/s3.md#how-it-stores-things) or [Azure Blob Storage](providers/azure-blob.md#how-it-stores-things) |
| `resourcefull.s3.stale_bin_entries`, `resourcefull.azureblob.stale_bin_entries`, `resourcefull.filesystem.stale_bin_entries` | {entry} | bin and purge entries a listing of the bin, a sweep or the daily scan for unnamed content dropped because their version was no longer in its [bin](deleting-safely.md#the-bin) |

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
| `upload` | `UploadAsync`, both overloads | `resourcefull.conditional`; `resourcefull.write_mode`: `create` or `replace`; a `resourcefull prune` child span for pruning older versions; with an [upload inspector](upload-inspection.md), a `resourcefull inspect` child span, with `resourcefull.inspection_outcome`; with SQL Server, `resourcefull.batches`: the batches of about a MiB its content was stored in |
| `upload_start` | `StartUploadAsync`, which starts an [upload session](uploading-in-parts.md) | |
| `upload_get` | `GetUploadAsync` | |
| `upload_append` | `AppendUploadAsync` | `resourcefull.offset`: where the append started |
| `upload_complete` | `CompleteUploadAsync` | `resourcefull.conditional`; `resourcefull.write_mode`: `create` or `replace`; a `resourcefull prune` child span; with an [upload inspector](upload-inspection.md), a `resourcefull inspect` child span |
| `upload_cancel` | `CancelUploadAsync` | |
| `restore` | `RestoreVersionAsync` | a `resourcefull prune` child span; with SQL Server, `resourcefull.batches` when it stores the version's content again, as for a version stored before 3.0 without a hash |
| `download` | `DownloadStreamingAsync`, `DownloadContentAsync` | for a range, `resourcefull.range`, as HTTP writes it: `100-199`, or `100-` to the end |
| `get_info` | `GetResourceInfoAsync` | |
| `exists` | `ExistsAsync` | |
| `get_usage` | `GetUsageAsync` | |
| `get_container_policy` | `GetPolicyAsync` | |
| `copy` | `CopyResourceAsync` | `resourcefull.destination_container`; `resourcefull.write_mode`: `create` or `replace`; a `resourcefull prune` child span; with SQL Server, `resourcefull.batches` when it stores the content again, as for a version stored before 3.0 without a hash |
| `move` | `MoveResourceAsync`, `RenameResourceAsync` | `resourcefull.destination_container` |
| `set_expiry` | `SetExpiryAsync` | |
| `set_metadata` | `SetResourceMetadataAsync` | |
| `set_retention` | `SetRetentionAsync` | |
| `set_legal_hold` | `SetLegalHoldAsync` | `resourcefull.legal_hold`: whether it set or lifted the hold |
| `set_content_type` | `SetContentTypeAsync` | |
| `set_container_metadata` | `SetMetadataAsync` | |
| `export` | `ExportContainerAsync` | `resourcefull.resources`, `resourcefull.versions`: what it wrote; `resourcefull.pages`: the pages of 1,000 names it read |
| `import` | `ImportContainerAsync`, from an export or from a stream of versions | `resourcefull.resources`, `resourcefull.versions`: what it stored; with an [upload inspector](upload-inspection.md), a `resourcefull inspect` child span for each version; with SQL Server, `resourcefull.batches`: the batches of about a MiB every version's content was stored in |
| `import_versions` | `ImportVersionsAsync` | `resourcefull.resources`, `resourcefull.versions`: what it stored |
| `copy_container` | `CopyContainerToAsync` | `resourcefull.destination_container`; `resourcefull.destination_store`, when the destination is a store with a name; `resourcefull.outcome`: `copied`, `updated` or `matching`; `resourcefull.resources`, `resourcefull.versions`: what it copied; `resourcefull.changed`, `resourcefull.deleted`, when it updated the container: how many versions it changed and deleted; the destination's `import` or `import_versions` as a child span |
| `copy_store` | `CopyStoreToAsync`, which [moves a store](moving-providers.md) | `resourcefull.destination_store`, as for `copy_container`; `resourcefull.copied`, `resourcefull.updated`, `resourcefull.matching`, `resourcefull.failed`: how many containers it copied, brought up to date, found matching and failed; a `copy_container` child span for each container |
| `prune_container` | `PruneAsync` | `resourcefull.pruned`: how many versions were past the limit |
| `expire` | `DeleteExpiredAsync`, and each sweep of [`UseExpiry`](expiry.md), when it finds expired versions, read-only containers' included | `resourcefull.expired`: how many versions it deleted; `resourcefull.batches`: how many batches of up to 100 it deleted them in; `resourcefull.batch_fallbacks`, when a batch failed and its versions were deleted one at a time instead: how many. Each failed batch's exception is recorded too, as on a failed operation, but the span isn't marked failed: the sweep carried on |
| `list_page` | `GetResourcePageAsync`, and each page `ListResourcesAsync` fetches | `resourcefull.page_size`; for a [search by metadata](listing.md#finding-resources-by-metadata), also `resourcefull.metadata_key`: the key searched, never its value; and with the file system, S3 and Azure Blob, `resourcefull.search_examined`: how many index entries the page read |
| `index_metadata` | `IndexMetadataAsync` | `resourcefull.indexed`: how many versions it indexed |
| `list_versions` | `GetResourceVersionsAsync` | |
| `list_top` | `GetTopVersionsAsync` | `resourcefull.order`: `largest_first` or `expiring_first` |
| `list_expired` | `GetExpiredVersionsAsync` | |
| `list_deleted` | `GetDeletedPageAsync`, and each page of the [bin](deleting-safely.md#the-bin) that `ListDeletedAsync` fetches | `resourcefull.page_size` |
| `undelete` | `UndeleteAsync` | |
| `purge_deleted` | `PurgeDeletedAsync` on a container client | |
| `purge` | `PurgeDueAsync` on the service client, and each sweep of [`UseExpiry`](expiry.md), when it finds versions due to leave a bin | `resourcefull.purged`: how many versions it purged |
| `delete` | `DeleteResourceIfExistsAsync` | `resourcefull.conditional`; when a [retained or held](deleting-safely.md) version refuses it, `resourcefull.refusal_reason` |
| `create_container` | `CreateContainerAsync`, `CreateContainerIfNotExistsAsync` | |
| `get_container` | `GetInfoAsync` | |
| `container_exists` | `ContainerExistsAsync` | |
| `list_containers` | `GetContainerPageAsync`, and each page `ListContainersAsync` fetches | `resourcefull.page_size` |
| `delete_container` | `DeleteContainerIfExistsAsync` | when a [retained or held](deleting-safely.md) version refuses it, `resourcefull.refusal_reason` |
| `migrate` | `MigrateResourcefullDatabaseAsync` (SQL Server) | span only; `resourcefull.schema`: the [schema](providers/sql-server.md#choosing-a-schema) given to `UseSqlServer`, when there is one |
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
A request that opens a [signed link](sharing-links.md), uploads through an [upload link](upload-links.md), or starts
a resumable upload through one, gets `resourcefull.link`: `valid`, `expired` or `invalid`. A link for a [named store](several-stores.md) also gets
`resourcefull.store`. A request to the [dashboard](aspnet-core/dashboard.md) gets `resourcefull.dashboard`, naming
what it asked for, such as `containers`, `largest` or, for a change, `upload`; the
[dashboard's telemetry](aspnet-core/dashboard.md#telemetry) lists them all. A request to a dashboard for a named store
also gets `resourcefull.store`. A request to the
[resumable upload endpoints](aspnet-core/resumable-uploads.md#telemetry) gets `resourcefull.tus`: `options`,
`create`, `resume`, `append`, `complete` or `cancel`, and, for a named store, `resourcefull.store`.
