# Resourcefull

Resourcefull stores and serves files of any size from your own infrastructure. Upload, download, list
and version **resources** in named **containers** through one client API, and keep the bytes in SQL
Server, in memory for tests, or in a storage provider of your own. If you know the Azure Blob Storage
SDK, the clients will feel familiar.

| Package | What it's for |
|---|---|
| `Resourcefull` | The clients and the provider interface. |
| `Resourcefull.SqlServer` | Stores resources in SQL Server. |
| `Resourcefull.InMemory` | Keeps resources in process memory, for tests and local development. |

## Features

- **Any size:** uploads and downloads stream in chunks, so memory use stays flat whatever the file size.
- **Versioning:** keep up to N versions of each resource, list them, download any of them, and restore
  an older one.
- **Integrity:** every version records its length and SHA-256, measured as it is written. An upload
  whose declared length doesn't match is rejected, and nothing is stored.
- **Safe under concurrency:** each name always has exactly one current version, and create-only uploads
  can't overwrite each other.
- **Your own storage:** implement `IResourceRepository` and register it with `UseProvider`.

## Getting started

```shell
dotnet add package Resourcefull
dotnet add package Resourcefull.SqlServer   # or Resourcefull.InMemory
```

Register Resourcefull with a provider:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!)
    .UseVersioning(maxVersions: 10));
```

Then use `IResourceServiceClient` for containers and `IResourceContainerClient` for the resources in one:

```csharp
public class Reports(IResourceServiceClient resources)
{
    public async Task<ResourceInfo> SaveAsync(string name, Stream content)
    {
        IResourceContainerClient reports = await resources.CreateResourceContainerIfNotExistsAsync("reports");
        return await reports.UploadAsync(name, content, new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("application/pdf", null)
        });
    }

    public async Task<Stream?> OpenAsync(string name)
    {
        ResourceDownloadResult? download = await resources.GetResourceContainerClient("reports").DownloadStreamingAsync(name);
        return download?.Stream;
    }
}
```

## SQL Server

Point `UseSqlServer` at the database that should hold Resourcefull's tables, either its own or your
application's. Provisioning turns on `READ_COMMITTED_SNAPSHOT` for that whole database, so give Resourcefull
a database of its own if your application relies on read-committed locking. To provision it from the
application, call `MigrateResourcefullDatabaseAsync`:

```csharp
var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
```

`Resourcefull.SqlServer` also ships idempotent scripts in its `scripts` folder. Run `Resourcefull.sql`
connected to the database to provision it. To bring an existing database up to date step by step, run the
scripts under `scripts/Migrations/` that it is missing, oldest first, after reading the
[upgrade guide](https://github.com/zacharydf7/Resourcefull/blob/main/docs/docs-v2-reference.md#upgrading-the-database-schema).
The [reference](https://github.com/zacharydf7/Resourcefull/blob/main/docs/docs-v2-reference.md#sql-server-provider)
covers provisioning and the recommended database settings.

## Documentation

The [reference documentation](https://github.com/zacharydf7/Resourcefull/blob/main/docs/docs-v2-reference.md)
covers every client method, the name rules, versioning, cancellation, errors and writing a provider.

## License

[MIT](https://github.com/zacharydf7/Resourcefull/blob/main/LICENSE)
