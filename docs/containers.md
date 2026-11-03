# Containers

Every resource lives in a **container**: a named group, such as `invoices` or `user-42`. Two resources can share a
name as long as they're in different containers. A container can carry metadata, a dictionary of strings you set when
you create it and can replace later with `SetContainerMetadataAsync` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>.

```csharp
IResourceContainerClient invoices = await resources.CreateContainerAsync("invoices",
    new Dictionary<string, string> { ["owner"] = "billing" });
```

| Method | When the container already exists | When it doesn't |
|---|---|---|
| `CreateContainerAsync` | throws `ResourceConflictException` | creates it |
| `CreateContainerIfNotExistsAsync` | returns a client for it, keeping its stored metadata | creates it |
| `GetContainerClient` | returns a client without contacting storage | returns a client; operations then report the container missing |
| `GetContainerInfoAsync` | returns its name, URI and metadata | returns `null` |
| `ContainerExistsAsync` | `true` | `false` |
| `DeleteContainerIfExistsAsync` | deletes it and every resource in it, returns `true` | returns `false` |
| `SetContainerMetadataAsync` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> | replaces its metadata, returns `true` | returns `false` |

Reads from a missing container return `null` or an empty list. An upload into one throws
`ResourceContainerNotFoundException`, even when the container was deleted a moment before the upload ran.

Deleting a large SQL Server container happens in batches that commit one at a time. If the delete is canceled or
fails partway, the versions it reached are gone, every other version is still whole, and each remaining name still has
a current version. The container still exists, so call the delete again to finish.

Metadata dictionaries are copied in both directions: changing one you passed in or got back doesn't change what's
stored. `SetContainerMetadataAsync` replaces the whole dictionary, including the `resourcefull.import` entry
that marks an [unfinished import](backup-and-move.md), so carry it over if it's there; an import still running
replaces the metadata again when it finishes.

## Listing containers

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Enumerate `ListContainersAsync` to list every container, or those whose names start with a prefix, ordered by name. It
fetches a page at a time as you go, so it suits a store with a container for each user:

```csharp
await foreach (ContainerInfo container in resources.ListContainersAsync(new ContainerListOptions { Prefix = "user-" }))
{
    Console.WriteLine(container.Name);
}
```

An API that pages for its own callers fetches one page per request with `GetContainerPageAsync` and hands back the
continuation token. Ordering, prefix matching, page sizes and continuation tokens work as they do when
[listing resources](listing.md), and as there, each page is a query of its own, with no snapshot.

In 3.x, `GetResourceContainersAsync` returned every container in one list; 4.0 removes it. For a small store,
`await resources.ListContainersAsync().ToListAsync()` still gives you them all in one list.
