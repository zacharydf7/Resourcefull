# Copying and moving

`CopyResourceAsync` copies a version, the current one unless you name another, to a name in the same or another
container. `MoveResourceAsync` moves a resource, every version of it, and renaming is a move within the container:

```csharp
ResourceInfo? copy = await container.CopyResourceAsync("report.pdf", "archive", "2026/report.pdf");
ResourceInfo? renamed = await container.MoveResourceAsync("draft.txt", container.ResourceContainerInfo.Name, "final.txt");
```

Both return `null` when the source doesn't exist.

- **A copy is a new version** at the destination. It has its own `VersionId` and `LastModified`, and the source's
  content, hash, type and metadata. It's stored like an upload: without [versioning](versioning.md), an existing name
  is refused unless you set `Overwrite`, and with it the copy becomes the name's new current version and older ones
  are pruned. `DestinationConditions` work as an upload's [conditions](concurrency.md#updating-only-if-unchanged) do.
- **A copy remembers its source.** `CopiedFrom` on its `ResourceInfo` names the container, name and version it was
  copied from, and stays even when that source is deleted. A restored version records the version it restored.
- **A move keeps everything:** every version, with its `VersionId`, `LastModified` and `CopiedFrom`, all at once. The
  destination name must not exist (`ResourceConflictException`), and `SourceConditions` let it go ahead only if the
  resource hasn't changed.
- **No content passes through your application** with SQL Server: the server copies the content, and a move only
  re-points the versions to their new container and name.
- [Container policies](container-policies.md) apply: a copy is checked as an upload to the destination, and a move needs
  a source that isn't read-only and a destination with room for every version it brings.
