# Telemetry

Resourcefull publishes a trace span for every operation, and metrics for durations and bytes, through .NET's
`ActivitySource` and `Meter`. Nothing is measured until something subscribes; until then, an operation does little
more than check whether anything is listening.

With OpenTelemetry, subscribe to both names:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(ResourcefullTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(ResourcefullTelemetry.MeterName));
```

Both names are `Resourcefull`. With SQL Server, SqlClient's own instrumentation, when you enable it, adds each
database call as a child of the operation's span.

## Metrics

| Instrument | Type | Unit | What it measures |
|---|---|---|---|
| `resourcefull.client.operation.duration` | histogram | s | each operation, from call to result |
| `resourcefull.client.uploaded` | counter | By | content bytes stored by uploads and restores |
| `resourcefull.client.downloaded` | counter | By | content bytes read from download streams, counted as they're read |

Every measurement carries these tags:

- **`resourcefull.operation`:** one of the operations below.
- **`resourcefull.provider`:** `sqlserver`, `inmemory`, or a custom provider's type name.
- **`error.type`:** the exception's type, on a failed operation only.

Container and resource names aren't metric tags: there can be any number of them, which would swamp a metrics backend.

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
| `download` | `DownloadStreamingAsync`, `DownloadContentAsync` | |
| `get_info` | `GetResourceInfoAsync` | |
| `exists` | `ExistsAsync` | |
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

`GetResourceContainerClient` doesn't contact storage, so it records nothing.

## ASP.NET Core

`Results.Resource` and `UploadFromRequestAsync` call the operations above, so they're covered through them. ASP.NET
Core records each request's status code, 304, 412 and 413 included, in its own metrics and spans. When a request has
preconditions, the package adds `resourcefull.precondition` to the request's span: `met`, `not_modified` or `failed`.
