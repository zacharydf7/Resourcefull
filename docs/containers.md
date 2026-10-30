# Containers

Every resource lives in a **container**: a named group, such as `invoices` or `user-42`. Two resources can share a
name as long as they're in different containers. A container can carry metadata, a dictionary of strings you set when
you create it.

```csharp
IResourceContainerClient invoices = await resources.CreateResourceContainerAsync("invoices",
    new Dictionary<string, string> { ["owner"] = "billing" });
```

| Method | When the container already exists | When it doesn't |
|---|---|---|
| `CreateResourceContainerAsync` | throws `ResourceConflictException` | creates it |
| `CreateResourceContainerIfNotExistsAsync` | returns a client for it, keeping its stored metadata | creates it |
| `GetResourceContainerClient` | returns a client without contacting storage | returns a client; operations then report the container missing |
| `GetResourceContainerInfoAsync` | returns its name, URI and metadata | returns `null` |
| `ContainerExistsAsync` | `true` | `false` |
| `DeleteResourceContainerIfExistsAsync` | deletes it and every resource in it, returns `true` | returns `false` |

Reads from a missing container return `null` or an empty list. An upload into one throws
`ResourceContainerNotFoundException`, even when the container was deleted a moment before the upload ran.

Deleting a large SQL Server container happens in batches that commit one at a time. If the delete is cancelled or
fails partway, the versions it reached are gone, every other version is still whole, and each remaining name still has
a current version. The container still exists, so call the delete again to finish.

Metadata dictionaries are copied in both directions: changing one you passed in or got back doesn't change what's
stored.
