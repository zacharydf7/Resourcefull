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
database. Here it happens at startup, which suits a single instance:

```csharp
using Resourcefull;
using Resourcefull.SqlServer;

builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!)
    .UseVersioning(maxVersions: 10));

var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
```

With several instances, provision from a deployment step instead: the first provisioning turns on a database setting
that briefly disconnects every other session. [SQL Server](providers/sql-server.md) covers both. `UseVersioning` is
optional; see [Versioning](versioning.md), and give every application that shares the database the same setting.

### Settings from configuration

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

`UseConfiguration` reads Resourcefull's settings from a section of your configuration, such as appsettings.json, so
they can differ between environments without a rebuild:

```json
{
  "Resourcefull": {
    "Versioning": { "MaxVersions": 10 },
    "Expiry": { "Interval": "00:01:00" },
    "Telemetry": { "RecordResourceNames": false },
    "ContainerPolicies": { "Defaults": { "MaxResourceSize": "100 MB" } }
  }
}
```

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!)
    .UseConfiguration(builder.Configuration.GetSection("Resourcefull")));
```

- **Each section turns its feature on:** `Versioning` as [`UseVersioning`](versioning.md) does, `Expiry` as
  [`UseExpiry`](expiry.md), `Telemetry` as [`UseTelemetry`](telemetry.md), and `ContainerPolicies` as
  [`UseContainerPolicies`](container-policies.md#defaults-from-configuration). An empty section, such as
  `"Versioning": {}`, turns it on with its defaults. Leave a section out to leave the feature off.
- **Times** are written as hours, minutes and seconds, such as `00:01:00` for a minute, and **sizes** as text, such as
  `100 MB`.
- **A key that isn't a setting, or a value it can't use,** stops the application at startup with an error naming it:
  a misspelled key such as `MaxVersion`, `"Versioning": false` where a section belongs, or `"MaxVersions": 0`. To
  leave a feature off, leave its section out.
- **Code wins** when it comes after: `.UseConfiguration(section).UseVersioning(maxVersions: 3)` keeps 3 versions
  whatever the section says.
- **The storage provider, the policy provider and the cleanup handler** are set in code, since each is a type of
  yours rather than a setting.

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

## Your first file API

Here's a complete ASP.NET Core app that stores files you send it and gives them back, using the
`Resourcefull.AspNetCore` package for the HTTP side:

```shell
dotnet new web -n Files
cd Files
dotnet add package Resourcefull.SqlServer
dotnet add package Resourcefull.AspNetCore
```

Point it at a database in `appsettings.json`. On Windows, LocalDB comes with Visual Studio; anywhere else, run SQL
Server in a container (`docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=Your_password1 -p 1433:1433
mcr.microsoft.com/mssql/server:2022-latest`) and use `Server=localhost;User Id=sa;Password=Your_password1;
TrustServerCertificate=True;Database=Files` instead:

```json
{
  "ConnectionStrings": {
    "Resources": "Server=(localdb)\MSSQLLocalDB;Database=Files;Trusted_Connection=True"
  }
}
```

Then replace `Program.cs`:

```csharp
using Resourcefull;
using Resourcefull.AspNetCore;
using Resourcefull.SqlServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!)
    .UseVersioning(maxVersions: 10));
// Missing files answer 404, a broken rule 400 or 409, rather than 500.
builder.Services.AddResourcefullExceptionHandler();

var app = builder.Build();
app.UseExceptionHandler();
await app.Services.MigrateResourcefullDatabaseAsync();

// The container the endpoints store into, created once.
using (IServiceScope scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerIfNotExistsAsync("files");

app.MapPut("/files/{**name}", (string name, HttpRequest request, IResourceServiceClient resources,
    CancellationToken cancellationToken) =>
    resources.GetResourceContainerClient("files").UploadFromRequestAsync(name, request, null, cancellationToken));

app.MapGet("/files/{**name}", (string name, IResourceServiceClient resources) =>
    Results.Resource(resources.GetResourceContainerClient("files"), name));

app.Run();
```

The download endpoint's `Results.Resource(container, name)`, which also answers range requests, is new in 3.2
<span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>. With 3.1, download first and pass the result, as [Serving downloads](aspnet-core/serving-downloads.md)
shows.

Run it with `dotnet run`, then send it a file and read it back, using the address `dotnet run` prints:

```shell
curl -X PUT -H "Content-Type: application/pdf" --data-binary @report.pdf http://localhost:5000/files/2026/report.pdf
curl -o copy.pdf http://localhost:5000/files/2026/report.pdf
```

The first `curl` answers `201 Created` with what was stored, the second downloads it. Uploading again keeps the
older version, because of `UseVersioning`.

This app lets anyone read and write every file, which is fine on your own machine. Before it goes anywhere else, read
[Securing file endpoints](aspnet-core/securing-endpoints.md) and [Going to production](going-to-production.md).

## In a console app or worker

Resourcefull registers its clients as scoped services, so outside a web request create a scope for each unit of work:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Resourcefull;
using Resourcefull.SqlServer;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!)
    .UseVersioning(maxVersions: 10)); // the same as every other application using this database
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

- [Going to production](going-to-production.md): what to set up before real users and real data arrive.
- [Uploads and downloads](uploading.md) for streams, lengths, hashes and cancellation.
- [ASP.NET Core](aspnet-core/index.md) for serving files from an API with the right status codes and
  headers.
- The [samples](../samples/README.md): a file service shaped like a production deployment, a minimal API and a
  Blazor app, which all run out of the box.
