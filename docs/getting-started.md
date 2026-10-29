# Getting started

Resourcefull needs .NET 10 or later.

## Install

Install the client package and one storage provider:

```shell
dotnet add package Resourcefull
dotnet add package Resourcefull.InMemory   # in process memory: tests and trying things out
dotnet add package Resourcefull.SqlServer  # SQL Server: production
```

## Register Resourcefull

`AddResourcefull` registers `IResourceServiceClient` with your application's services. Choose exactly one provider
inside it:

```csharp
using Resourcefull;
using Resourcefull.InMemory;

builder.Services.AddResourcefull(options => options.UseInMemory());
```

For SQL Server, pass the connection string of the database that will hold Resourcefull's tables, and provision that
database once, here at startup:

```csharp
using Resourcefull;
using Resourcefull.SqlServer;

builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!)
    .UseVersioning(maxVersions: 10));

var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
```

[SQL Server](providers/sql-server.md) covers choosing the database and provisioning it from a deployment step
instead. `UseVersioning` is optional; see [Versioning](versioning.md).

## Store and read a file

Inject `IResourceServiceClient`. It manages **containers**; a container client works with the **resources** in one
container.

```csharp
public class Invoices(IResourceServiceClient resources)
{
    public async Task<ResourceInfo> SaveAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        IResourceContainerClient invoices =
            await resources.CreateResourceContainerIfNotExistsAsync("invoices", null, cancellationToken);

        return await invoices.UploadAsync(fileName, content, new ResourceUploadOptions
        {
            Overwrite = true,
            Headers = new ResourceHeaders("application/pdf", null)
        }, cancellationToken);
    }

    public async Task<ResourceDownloadResult?> OpenAsync(string fileName, CancellationToken cancellationToken)
        => await resources.GetResourceContainerClient("invoices").DownloadStreamingAsync(fileName, null, cancellationToken);
}
```

- `UploadAsync` reads the stream from its current position to its end and returns what it stored, including the
  measured `ContentLength` and a SHA-256 `ContentHash`.
- `DownloadStreamingAsync` returns `null` when there's no such resource. Otherwise its `Stream` reads the content
  from storage as you read it, so memory use stays flat whatever the file size. Dispose the stream when you're done.
- `GetResourceContainerClient` doesn't contact storage, so it's cheap to call per request. A container client is a
  lightweight handle: there's nothing to dispose.

List, inspect and delete with the same client:

```csharp
IResourceContainerClient invoices = resources.GetResourceContainerClient("invoices");

IReadOnlyList<ResourceInfo> all = await invoices.GetResourcesAsync();
ResourceInfo? info = await invoices.GetResourceInfoAsync("2026-10.pdf");
bool deleted = await invoices.DeleteResourceIfExistsAsync("2026-10.pdf");
```

## In a console app or worker

Resourcefull registers its clients as scoped services, so outside a web request create a scope for each unit of work:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Resourcefull;
using Resourcefull.SqlServer;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddResourcefull(options => options.UseSqlServer(builder.Configuration.GetConnectionString("Resources")!));
using IHost host = builder.Build();
await host.Services.MigrateResourcefullDatabaseAsync();

using (IServiceScope scope = host.Services.CreateScope())
{
    var resources = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
    IResourceContainerClient reports = await resources.CreateResourceContainerIfNotExistsAsync("reports");

    await using FileStream file = File.OpenRead("summary.csv");
    await reports.UploadAsync("summary.csv", file, new ResourceUploadOptions { Overwrite = true });
}
```

## Next steps

- [Uploads and downloads](uploads-and-downloads.md) for streams, lengths, hashes and cancellation.
- [ASP.NET Core](recipes/aspnet-core.md) for serving files from an API with the right status codes and
  headers.
- The [samples](../samples/README.md): a minimal API and a Blazor app that run out of the box.
