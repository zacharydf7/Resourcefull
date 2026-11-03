# Copying and moving

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

`CopyResourceAsync` copies a version, the current one unless you name another, to a name in the same or another
container. `MoveResourceAsync` moves a resource, every version of it, and `RenameResourceAsync` is a move within the
container:

```csharp
ResourceInfo? copy = await container.CopyResourceAsync("report.pdf", "archive", "2026/report.pdf");
ResourceInfo? moved = await container.MoveResourceAsync("draft.txt", "published", "final.txt");
ResourceInfo? renamed = await container.RenameResourceAsync("draft.txt", "final.txt");
```

All three return `null` when the source doesn't exist. A rename is a move to `container.Name`, so everything below
about moves applies to it too.

- **A copy is a new version** at the destination. It has its own `VersionId` and `LastModified`, and the source's
  content, hash, type and metadata. It's stored like an upload: an existing name is refused unless you set
  `Mode = ResourceWriteMode.Replace`, and then the copy becomes the name's new current version and older ones are
  pruned past the destination's [version limit](versioning.md). `DestinationConditions` work as an upload's
  [conditions](concurrency.md#updating-only-if-unchanged) do, and need `Replace`.
- **A copy remembers its source.** `CopiedFrom` on its `ResourceInfo` names the container, name and version it was
  copied from, and stays even when that source is deleted. A restored version records the version it restored.
- **A move keeps everything:** every version, with its `VersionId`, `LastModified` and `CopiedFrom`, all at once. The
  destination name must not exist (`ResourceConflictException`), and `SourceConditions` let it go ahead only if the
  resource hasn't changed.
- **No content passes through your application** with SQL Server: the server copies the content, and a move only
  re-points the versions to their new container and name. The exception is copying a version stored before 3.0 that
  has no hash yet, which is read through the application until the
  [hash backfill](providers/sql-server-upgrades.md#recording-hashes-for-older-versions) records it.
- [Container policies](container-policies.md) apply: a copy is checked as an upload to the destination, and a move needs
  a source that isn't read-only and a destination with room for every version it brings.
- **With the [file-system](providers/file-system.md) provider,** a rename copies nothing, but a move to another
  container copies each version's content through your application, so it takes longer the larger the versions are.
- **With the [S3](providers/s3.md) and [Azure Blob](providers/azure-blob.md) providers,** a rename copies nothing, but
  a move to another container has the store copy each version's content, so it takes longer the larger the versions
  are. The content still doesn't pass through your application. Their pages explain how a move stays all or nothing
  without transactions.
