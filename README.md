# Resourcefull

Resourcefull stores and serves files of any type and size from infrastructure you run yourself. Upload, download,
list and version **resources** in named **containers** through one client API, and keep the bytes in SQL Server, in
memory for tests, or in a storage provider of your own.

| Package | What it's for |
|---|---|
| `Resourcefull` | The clients and the provider interface. |
| `Resourcefull.SqlServer` | Stores resources in SQL Server. |
| `Resourcefull.InMemory` | Keeps resources in process memory, for tests and local development. |
| `Resourcefull.AspNetCore` | Serves downloads and accepts uploads in ASP.NET Core, with conditional requests and status codes. |

## Features

- **Any size:** uploads and downloads stream, so memory use stays flat whatever the file size.
- **Versioning:** keep up to N versions of each resource, list them, download any of them, and restore an older one.
- **Integrity:** every version records its length and SHA-256 hash, measured as it's written. An upload whose
  declared length doesn't match its content is rejected, and nothing is stored.
- **Safe under concurrency:** each name always has exactly one current version, and create-only uploads can't
  overwrite each other.
- **Your own storage:** implement `IResourceRepository` and register it with `UseProvider`.
- **Made for ASP.NET Core:** serve downloads with ETags and conditional requests, accept uploads with preconditions,
  and turn exceptions into the right status codes.

Coming in 3.2, and documented on the site already:

- **Container policies:** size limits, quotas, allowed file types and read-only containers, per container.
- **Copy, move and rename,** within a container or between containers, keeping where each copy came from.
- **Expiring files:** uploads that delete themselves, with a hook to audit every deletion.
- **Signed links:** let anyone download one file, without signing in, until a time you choose.
- **Backups:** export a container, every version, to a zip file, and import it into another store.
- **Range downloads:** read part of a file, so videos can seek and downloads can resume.
- **Health checks and telemetry:** OpenTelemetry spans and metrics for every operation.

## Quick start

```shell
dotnet add package Resourcefull
dotnet add package Resourcefull.SqlServer   # or Resourcefull.InMemory
```

Register Resourcefull with a provider, and provision the database (here at startup, which suits a single instance;
see the [SQL Server docs](https://zacharydf7.github.io/Resourcefull/docs/providers/sql-server.html) for deployment
steps):

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

The [documentation site](https://zacharydf7.github.io/Resourcefull/) has guides for every feature, grouped by what
you're trying to do, plus the API reference.

- [Introduction](https://zacharydf7.github.io/Resourcefull/docs/index.html): containers, resources, versions and
  providers
- [Getting started](https://zacharydf7.github.io/Resourcefull/docs/getting-started.html)
- [Uploading](https://zacharydf7.github.io/Resourcefull/docs/uploading.html) and
  [downloading](https://zacharydf7.github.io/Resourcefull/docs/downloading.html) files
- [ASP.NET Core](https://zacharydf7.github.io/Resourcefull/docs/aspnet-core/index.html): serving downloads, accepting
  uploads and sharing files with links
- [SQL Server](https://zacharydf7.github.io/Resourcefull/docs/providers/sql-server.html): provisioning, permissions
  and upgrades
- [Upgrading to 3.0](https://zacharydf7.github.io/Resourcefull/docs/upgrading.html) and the
  [changelog](https://github.com/zacharydf7/Resourcefull/blob/main/CHANGELOG.md)

## Contributing

See [CONTRIBUTING.md](https://github.com/zacharydf7/Resourcefull/blob/main/CONTRIBUTING.md).

## License

[MIT](https://github.com/zacharydf7/Resourcefull/blob/main/LICENSE)
