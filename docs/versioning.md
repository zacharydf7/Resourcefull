# Versioning

Without versioning, each name holds one version: an upload with `Overwrite` replaces it. With versioning, every upload
of a name stores a new **version**, and the older ones stay until there are more than you keep.

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseVersioning(maxVersions: 10)); // keeps 100 when you don't say
```

## Current and older versions

Each name has exactly one **current** version: the one stored most recently. Reads without a `versionId` return it,
and `ResourceInfo.IsLatest` is `true` on it and only on it.

```csharp
IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("contract.docx");
ResourceInfo current = versions[0];                          // the current version comes first
IEnumerable<ResourceInfo> history = versions.Where(v => !v.IsLatest); // then newest to oldest

ResourceDownloadResult? older = await container.DownloadStreamingAsync("contract.docx", history.First().VersionId);
```

`GetResourcesAsync` lists only the current version of each name.

A `VersionId` is an opaque string: compare it for equality, and don't parse it or sort by it. Storage decides the
order versions were stored in, so servers with skewed clocks can't make an older upload current. `LastModified` is
when a version became current, by the storing server's clock, and is for display.

## Keeping a limited number

After each upload, Resourcefull deletes the oldest versions beyond `maxVersions`, counting the current one. Uploads
don't wait for each other's pruning, but each prune looks at the versions as they stand after its own write, so
together they never leave more than the limit when every prune succeeds.

Pruning happens after the new version is stored, so it never fails an upload. If it fails, for example because the
database is briefly unreachable, the upload still returns the stored version, and the extra versions stay until a later
upload of the name prunes them. Until then they're ordinary versions you can list, read and delete. A failed prune is
logged as a warning, with its exception, under the `Resourcefull.ResourceContainerClient` category, when your
application has logging set up, so one that keeps failing doesn't go unnoticed.

## Restoring

`RestoreVersionAsync` copies an older version into a new current version. The older version stays where it is, so
restoring never loses history:

```csharp
ResourceInfo? restored = await container.RestoreVersionAsync("contract.docx", versionId);
if (restored is null) { /* no such name or version */ }
```

The restored version is measured like any upload: it gets its own `VersionId`, `ContentLength`, `ContentHash` and
`LastModified`, and the older version's content type and metadata. Its `CopiedFrom` names the version it restored, so
a history view can show it. `RestoreVersionAsync` throws
`InvalidOperationException` when versioning is off.

## Deleting

| Call | Deletes |
|---|---|
| `DeleteResourceIfExistsAsync(name)` | the name and every version of it |
| `DeleteResourceIfExistsAsync(name, versionId)` | that one version; if it was current, the most recently stored remaining version becomes current |
| `DeleteResourceContainerIfExistsAsync(container)` | the container and everything in it |

Each returns `true` if it deleted anything.
