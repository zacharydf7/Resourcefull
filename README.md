# Resourcefull

Resourcefull stores and serves files of any type and size from infrastructure you run yourself. Upload, download,
list and version **resources** in named **containers** through one client API, and keep the bytes in SQL Server, in
memory for tests, or in a storage provider of your own.

| Package | What it's for |
|---|---|
| `Resourcefull` | The clients and the provider interface. |
| `Resourcefull.SqlServer` | Stores resources in SQL Server. |
| `Resourcefull.InMemory` | Keeps resources in process memory, for tests and local development. |

## Features

- **Any size:** uploads and downloads stream, so memory use stays flat whatever the file size.
- **Versioning:** keep up to N versions of each resource, list them, download any of them, and restore an older one.
- **Integrity:** every version records its length and SHA-256 hash, measured as it's written. An upload whose
  declared length doesn't match its content is rejected, and nothing is stored.
- **Safe under concurrency:** each name always has exactly one current version, and create-only uploads can't
  overwrite each other.
- **Your own storage:** implement `IResourceRepository` and register it with `UseProvider`.

## Quick start

```shell
dotnet add package Resourcefull
dotnet add package Resourcefull.SqlServer   # or Resourcefull.InMemory
```

Register Resourcefull with a provider, and provision the database:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!)
    .UseVersioning(maxVersions: 10));

var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
```

Then use `IResourceServiceClient` for containers, and a container client for the resources in one:

```csharp
public class Reports(IResourceServiceClient resources)
{
    public async Task<ResourceInfo> SaveAsync(string name, Stream content)
    {
        IResourceContainerClient reports = await resources.CreateResourceContainerIfNotExistsAsync("reports");
        return await reports.UploadAsync(name, content, new ResourceUploadOptions
        {
            Overwrite = true,
            Headers = new ResourceHeaders("application/pdf", null)
        });
    }

    public async Task<ResourceDownloadResult?> OpenAsync(string name)
        => await resources.GetResourceContainerClient("reports").DownloadStreamingAsync(name);
}
```

## Documentation

- [Getting started](https://github.com/zacharydf7/Resourcefull/blob/main/docs/getting-started.md)
- [Concepts](https://github.com/zacharydf7/Resourcefull/blob/main/docs/index.md#concepts): containers and names,
  uploads and downloads, versioning, concurrency, errors
- [SQL Server](https://github.com/zacharydf7/Resourcefull/blob/main/docs/providers/sql-server.md): provisioning,
  permissions and upgrades
- [ASP.NET Core recipes](https://github.com/zacharydf7/Resourcefull/blob/main/docs/recipes/aspnet-core.md)
- [Upgrading to 3.0](https://github.com/zacharydf7/Resourcefull/blob/main/docs/upgrading.md) and the
  [changelog](https://github.com/zacharydf7/Resourcefull/blob/main/CHANGELOG.md)
- [All documentation](https://github.com/zacharydf7/Resourcefull/blob/main/docs/index.md)

## Contributing

See [CONTRIBUTING.md](https://github.com/zacharydf7/Resourcefull/blob/main/CONTRIBUTING.md).

## License

[MIT](https://github.com/zacharydf7/Resourcefull/blob/main/LICENSE)
