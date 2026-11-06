# Several stores

Most applications need one store: one place their files live, set up with `AddResourcefull`. Some need more than one:

- **Hot and archive storage.** Recent files in a fast database, and older ones in a cheaper one.
- **A database for each tenant,** so one customer's files never share a database with another's.
- **Moving to another provider gradually,** reading from the old store while new files go to the new one.

`AddKeyedResourcefull` adds a store under a name, beside the one `AddResourcefull` adds or on its own. Each store is
configured the same way, with its own provider and settings:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Resources")!));

builder.Services.AddKeyedResourcefull("archive", options => options
    .UseSqlServer(builder.Configuration.GetConnectionString("Archive")!)
    .UseVersioning(maxVersions: 10));
```

Ask for a named store's client as a
[keyed service](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#keyed-services), by its name:

```csharp
public sealed class InvoiceArchive([FromKeyedServices("archive")] IResourceServiceClient archive)
{
    public async Task SaveAsync(string name, Stream pdf, CancellationToken cancellationToken)
    {
        IResourceContainerClient invoices =
            await archive.CreateContainerIfNotExistsAsync("invoices", cancellationToken: cancellationToken);
        await invoices.UploadAsync(name, pdf, cancellationToken: cancellationToken);
    }
}
```

The store `AddResourcefull` adds has no name, so a plain `IResourceServiceClient` is that one, as before.

## What each store has of its own

Everything you give a store's options belongs to that store alone:

- its provider, and with SQL Server, its database and [schema](providers/sql-server.md#choosing-a-schema);
- versioning, [container policies](container-policies.md) and the policy provider;
- the [URI provider](uri-providers.md), the [upload inspector](upload-inspection.md) and the cleanup handler;
- [expiry](expiry.md): each store that calls `UseExpiry` gets a sweep of its own, on its own interval;
- what [telemetry](telemetry.md) records.

Two stores can use the same provider with different databases, or different providers. Containers belong to their
store: a container named `invoices` in one store has nothing to do with one of the same name in another. Copies and
moves work within one store; to move a file to another store, download it from one and upload it to the other.

A store can be added only once. Calling `AddResourcefull` a second time, or `AddKeyedResourcefull` with a name already
added, throws `InvalidOperationException`, rather than mixing two configurations into one store. Names are compared
exactly, so `archive` and `Archive` are two stores.

## SQL Server

Provision each store's database. `MigrateResourcefullDatabaseAsync` and `GenerateResourcefullScript` take the store's
name, and without one work on the store `AddResourcefull` adds:

```csharp
var app = builder.Build();
await app.Services.MigrateResourcefullDatabaseAsync();
await app.Services.MigrateResourcefullDatabaseAsync("archive");
```

Two stores can share a database if each has its tables in a [schema](providers/sql-server.md#choosing-a-schema) of
its own. Each store also has its own [`ContentHashBackfill`](providers/sql-server-upgrades.md#recording-hashes-for-older-versions).
For a named store, resolve it from a scope by the store's name, with
`GetRequiredKeyedService<ContentHashBackfill>("archive")`. It's in the `Resourcefull.SqlServer` namespace.

## Health checks

The [health checks](health-checks.md) take the store's name too. Give each store's check a name of its own:

```csharp
builder.Services.AddHealthChecks()
    .AddResourcefullSqlServerCheck(tags: ["ready"])
    .AddResourcefullSqlServerCheck("resourcefull-archive", tags: ["ready"], storeName: "archive");
```

## Telemetry

Spans and metrics from a named store carry a `resourcefull.store` tag with the store's name, so a dashboard can tell
the archive's latency from the hot store's. The store `AddResourcefull` adds records no such tag, so adding a second
store doesn't change what the first one records.

## Signed links

[Download links](sharing-links.md) and [upload links](upload-links.md) work with every store. Add a link signer for
each store whose links you serve:

```csharp
builder.Services.AddResourcefullLinks();          // the store AddResourcefull adds
builder.Services.AddResourcefullLinks("archive"); // the store named "archive"
```

A named store's signer is a keyed service. Create its links with it, and give the endpoint that serves them that
store's client:

```csharp
app.MapPost("/archive/share/{container}/{**name}", async (string container, string name,
    [FromKeyedServices("archive")] IResourceLinkSigner links, CancellationToken cancellationToken) =>
{
    string? token = await links.CreateDownloadTokenAsync(new ResourceDownloadLink(container, name), cancellationToken);
    return token is null
        ? Results.NotFound()
        : Results.Ok(new { url = $"https://example.com/archive/shared/{token}" });
}).RequireAuthorization();

app.MapMethods("/archive/shared/{token}", ["GET", "HEAD"], (string token,
    [FromKeyedServices("archive")] IResourceServiceClient archive) => Results.SharedDownload(archive, token));
```

`Results.SharedDownload` and `Results.SharedUpload` check the token with the signer of the store whose client they're
given. A token opens only with the signer of the store that made it, so a link made for one store gets 404 at another
store's endpoint, even when both stores have a file at the same container and name. Map one endpoint for each store's
links.

A named store's tokens are sealed with its name, so renaming the store makes the links it made stop working, as
removing the Data Protection keys does. Adding a named store doesn't change the links of the store `AddResourcefull`
adds.

`Results.Resource` and the [exception handler](aspnet-core/status-codes.md) work with every store. So do the
[resumable upload endpoints](aspnet-core/resumable-uploads.md) and the [dashboard](aspnet-core/dashboard.md), through a
`StoreName` option on each.
