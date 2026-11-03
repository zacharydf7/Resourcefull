# Resourcefull

[![NuGet](https://img.shields.io/nuget/v/Resourcefull.svg)](https://www.nuget.org/packages/Resourcefull)
[![Build](https://github.com/zacharydf7/Resourcefull/actions/workflows/build.yml/badge.svg)](https://github.com/zacharydf7/Resourcefull/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/zacharydf7/Resourcefull/blob/main/LICENSE)

Resourcefull stores and serves files of any type and size from infrastructure you run yourself: the SQL Server or
PostgreSQL database you already have, or a directory on your own disks, with nothing new to deploy. Upload, download,
list and version **resources** in named **containers** through one client API. When you move to the cloud, your code
comes with you: change the line that picks the provider to keep the bytes in Amazon S3, another S3-compatible store or
Azure Blob Storage instead. Tests can keep the bytes in memory, and a storage provider of your own can keep them
anywhere else.

| Package | What it's for |
|---|---|
| `Resourcefull` | The clients and the provider interface. |
| `Resourcefull.SqlServer` | Stores resources in SQL Server. |
| `Resourcefull.PostgreSql` | Stores resources in PostgreSQL. |
| `Resourcefull.Sqlite` | Stores resources in a SQLite database file. |
| `Resourcefull.FileSystem` | Stores resources in a directory, on a local disk or a network share. |
| `Resourcefull.S3` | Stores resources in Amazon S3 or another S3-compatible store. |
| `Resourcefull.AzureBlob` | Stores resources in Azure Blob Storage. |
| `Resourcefull.InMemory` | Keeps resources in process memory, for tests and local development. |
| `Resourcefull.AspNetCore` | Serves downloads and accepts uploads in ASP.NET Core, with conditional requests and status codes. |
| `Resourcefull.Testing` | The tests every provider must pass, for checking a provider of your own. |

## Features

- **Any size:** uploads and downloads stream, so memory use stays flat whatever the file size.
- **Versioning:** keep up to N versions of each resource, list them, download any of them, and restore an older one.
- **Integrity:** every version records its length and SHA-256 hash, measured as it's written. An upload whose
  declared length doesn't match its content is rejected, and nothing is stored.
- **Safe under concurrency:** each name always has exactly one current version, and create-only uploads can't
  overwrite each other.
- **Your own storage:** implement `IResourceRepository` and register it with `UseProvider` (in 4.0, derive from
  `ResourceRepository` instead).
- **Made for ASP.NET Core:** serve downloads with ETags and conditional requests, accept uploads with preconditions,
  and turn exceptions into the right status codes.

Coming in 4.0, and documented on the site already:

- **Container policies:** size limits, quotas, allowed file types and read-only containers, per container.
- **Upload inspection:** scan uploads for malware, or check their type, before they're stored.
- **Copy, move and rename,** within a container or between containers, keeping where each copy came from.
- **Expiring files:** uploads that delete themselves, with a hook to audit every deletion.
- **Signed links:** let anyone download one file, or upload files within limits you set, without signing in, until a
  time you choose.
- **A dashboard:** one line maps a page showing the store's health and each container's usage against its limits.
- **Backups:** export a container, every version, to a zip file, and import it into another store.
- **Range downloads:** read part of a file, so videos can seek and downloads can resume.
- **Health checks and telemetry:** OpenTelemetry spans and metrics for every operation.

## Quick start

```shell
dotnet add package Resourcefull
dotnet add package Resourcefull.SqlServer   # or Resourcefull.PostgreSql, or Resourcefull.InMemory
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
        IResourceContainerClient reports = await resources.CreateContainerIfNotExistsAsync("reports");
        return await reports.UploadAsync(name, content, new ResourceUploadOptions
        {
            Mode = ResourceWriteMode.Replace,
            Headers = new ResourceHeaders("application/pdf", null)
        });
    }

    public async Task<ResourceDownloadResult?> OpenAsync(string name)
        => await resources.GetContainerClient("reports").DownloadStreamingAsync(name);
}
```

## Documentation

The [documentation site](https://zacharydf7.github.io/Resourcefull/) has guides for every feature, grouped by what
you're trying to do, plus the API reference. Deciding between Resourcefull, object storage and the file system? See
[When to use Resourcefull](https://zacharydf7.github.io/Resourcefull/docs/when-to-use.html).

- [Introduction](https://zacharydf7.github.io/Resourcefull/docs/index.html): containers, resources, versions and
  providers
- [Getting started](https://zacharydf7.github.io/Resourcefull/docs/getting-started.html)
- [Uploading](https://zacharydf7.github.io/Resourcefull/docs/uploading.html) and
  [downloading](https://zacharydf7.github.io/Resourcefull/docs/downloading.html) files
- [ASP.NET Core](https://zacharydf7.github.io/Resourcefull/docs/aspnet-core/index.html): serving downloads, accepting
  uploads and sharing files with links
- [SQL Server](https://zacharydf7.github.io/Resourcefull/docs/providers/sql-server.html): provisioning, permissions
  and upgrades
- [PostgreSQL](https://zacharydf7.github.io/Resourcefull/docs/providers/postgresql.html): provisioning, permissions
  and how it differs from SQL Server
- [SQLite](https://zacharydf7.github.io/Resourcefull/docs/providers/sqlite.html): choosing the file, one writer at a
  time, provisioning and backups
- [File system](https://zacharydf7.github.io/Resourcefull/docs/providers/file-system.html): choosing a directory,
  sharing it between servers, backups and costs
- [S3-compatible storage](https://zacharydf7.github.io/Resourcefull/docs/providers/s3.html): connecting to a store,
  sharing a bucket, permissions and costs
- [Azure Blob Storage](https://zacharydf7.github.io/Resourcefull/docs/providers/azure-blob.html): connecting to an
  account, permissions, account settings and costs
- [Upgrading](https://zacharydf7.github.io/Resourcefull/docs/upgrading.html), including from 2.x, and the
  [changelog](https://github.com/zacharydf7/Resourcefull/blob/main/CHANGELOG.md)

## Contributing

See [CONTRIBUTING.md](https://github.com/zacharydf7/Resourcefull/blob/main/CONTRIBUTING.md).

## License

[MIT](https://github.com/zacharydf7/Resourcefull/blob/main/LICENSE)
