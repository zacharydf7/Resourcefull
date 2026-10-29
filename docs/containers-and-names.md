# Containers and names

## Containers

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

## Names

Container and resource names follow the same rules on every provider:

- **Case-sensitive.** `Report.pdf` and `report.pdf` are different resources, and `Docs` and `docs` are different
  containers, whatever the SQL Server's collation.
- **Length:** a container name has 1 to 128 characters (`ResourceNames.MaxContainerNameLength`), and a resource name
  1 to 256 (`ResourceNames.MaxResourceNameLength`).
- **No whitespace at either end.** SQL Server ignores trailing spaces when it compares strings, so names that differ
  only there are rejected rather than treated as the same name.

Any other character is allowed, including `/`, so `2026/10/report.pdf` is a valid resource name. Containers don't nest,
though: the slash is just part of the name.

Every method that takes a name checks it before contacting storage, and throws `ArgumentNullException` for `null` and
`ArgumentException` for a name that breaks the rules, so an API can turn that into a 400. To check a name yourself
first, for example to validate a whole request before acting on any of it, call `ResourceNames.IsValidContainerName`
or `ResourceNames.IsValidResourceName`.

Names appear in URIs and file names that you build from them, so escape them there; see
[URI providers](uri-providers.md) and the [ASP.NET Core recipes](recipes/aspnet-core.md).
