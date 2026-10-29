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

`Resourcefull.SqlServer` ships idempotent provisioning and upgrade scripts. Run `Scripts/Resourcefull.sql`
to create the database. To bring an existing database up to date, run the scripts under
`Scripts/Migrations/` that it is missing, oldest first, after reading the
[upgrade guide](https://github.com/zacharydf7/Resourcefull/blob/main/docs/docs-v2-reference.md#upgrading-the-database-schema).
The [reference](https://github.com/zacharydf7/Resourcefull/blob/main/docs/docs-v2-reference.md#sql-server-provider)
covers provisioning and the recommended database settings.

## Documentation

The [reference documentation](https://github.com/zacharydf7/Resourcefull/blob/main/docs/docs-v2-reference.md)
covers every client method, the name rules, versioning, cancellation, errors and writing a provider.

## License

[MIT](https://github.com/zacharydf7/Resourcefull/blob/main/LICENSE)
