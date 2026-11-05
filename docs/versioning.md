# Versioning

Every upload stores a new **version** of its name. Uploads replace an existing name only when they ask to, with
`ResourceWriteMode.Replace`; by default, an upload of a name that exists is refused (see
[Uploading](uploading.md#replacing-a-file)). With versioning, the version an upload replaces stays as an older
version until the name has more than you keep.

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseVersioning(maxVersions: 10)); // keeps 100 when you don't say

await container.UploadAsync("contract.docx", file, new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
```

Without versioning, a container keeps one version of each name, so a replacing upload deletes the version it
replaced. A [container policy](container-policies.md) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> can give a container a limit of its own with
`MaxVersions`, such as keeping the history of contracts but not of thumbnails; `UseVersioning` is the default for
containers whose policy doesn't set one.

<span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> In 3.1, versioning is store-wide: with it, every upload of a name adds a version; without it, an upload
with `Overwrite` replaces the name's only version.

## Current and older versions

Each name has exactly one **current** version: the one stored most recently. Reads without a `versionId` return it,
and `ResourceInfo.IsLatest` is `true` on it and only on it.

```csharp
IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("contract.docx");
ResourceInfo current = versions[0];                          // the current version comes first
IEnumerable<ResourceInfo> history = versions.Where(v => !v.IsLatest); // then newest to oldest

ResourceDownloadResult? older = await container.DownloadStreamingAsync("contract.docx", history.First().VersionId);
```

`ListResourcesAsync` lists only the current version of each name.

A `VersionId` is an opaque string: compare it for equality, and don't parse it or sort by it. Storage decides the
order versions were stored in, so servers with skewed clocks can't make an older upload current. `LastModified` is
when a version became current, by the storing server's clock, and is for display.

## Keeping a limited number

After each upload, Resourcefull deletes the oldest versions beyond the container's limit, counting the current one. Uploads
don't wait for each other's pruning, but each prune looks at the versions as they stand after its own write, so
together they never leave more than the limit when every prune succeeds.

Pruning happens after the new version is stored, so it never fails an upload. If it fails, for example because the
database is briefly unreachable, the upload still returns the stored version, and the extra versions stay until a later
upload of the name prunes them. Until then they're ordinary versions you can list, read and delete. A failed prune is
logged as a warning, with its exception, under the `Resourcefull.ResourceContainerClient` category, when your
application has logging set up, so one that keeps failing doesn't go unnoticed.

To prune a whole container at once <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, such as after fixing what made prunes fail, or after an
[import](backup-and-move.md), which keeps every version it's given, call the container client's `PruneAsync`. It
prunes every name as an upload would, and returns how many versions were past the limit:

```csharp
int pruned = await resources.GetContainerClient("contracts").PruneAsync();
```

With [expiry](expiry.md) turned on <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, pruning doesn't delete a version at once: it sets it to expire five
minutes later, and the expiry sweep deletes it then. A download already reading it can finish, rather than fail
partway. Until it's deleted, it's listed, readable and counted toward [container policies](container-policies.md)
like any version, with its `ExpiresAt` showing when it goes. A version that already expires sooner keeps its expiry.
In a container that keeps one version, deleting the current version, or its expiring, deletes the versions it replaced
that are still waiting too, so replaced content doesn't come back; if deleting them fails, the failure is logged as a
failed prune and they stay until they expire. In a container that keeps more, deleting the current version makes the
previous one current, as always, and that can be one waiting to be deleted.

In a container with a [bin](deleting-safely.md#the-bin) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, a pruned version goes to the bin at once, without
the five minutes, since its content stays readable there; it can be brought back until the bin's time is up.

A version that's [retained or under a legal hold](deleting-safely.md) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> isn't pruned: it stays past the limit,
and once it's no longer protected, the next prune removes it. So an upload that replaces a retained version always succeeds,
and the retained version stays on as an older one, even in a container that keeps one version.

To record the versions that pruning deletes, such as for an audit, register a
[cleanup handler](expiry.md#auditing-whats-deleted) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>.

## Changing the setting

The limit is a setting of each application, and of the policies it gives containers, not something stored with the
files. Every application that writes to the same store applies its own limit to the names it uploads:

- **An application without `UseVersioning` or a policy's `MaxVersions` keeps one version per name,** so its
  next replacing upload of a name deletes every older version of that name.
- **Lowering the limit** deletes each name's extra versions the next time that name is uploaded, not straight away;
  `PruneAsync` applies it to a whole container at once.

So give every application that shares a store, workers and scheduled jobs included, the same `UseVersioning` setting
and policies. To turn versioning off for good, expect each name's history to go at its next upload; a
[cleanup handler](expiry.md#auditing-whats-deleted) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> hears about every version deleted that way.

## Restoring

`RestoreVersionAsync` copies an older version into a new current version. The older version stays where it is, so
restoring never loses history:

```csharp
ResourceInfo? restored = await container.RestoreVersionAsync("contract.docx", versionId);
if (restored is null) { /* no such name or version */ }
```

The restored version is measured like any upload: it gets its own `VersionId`, `ContentLength`, `ContentHash` and
`LastModified`, and the older version's content type and metadata. From 4.0 <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>, a restore is a copy within the store,
as [copying](copying-and-moving.md) is: with SQL Server, the content doesn't pass through your application, and a
transient failure is retried rather than ending the restore. (A version stored before 3.0 is still read through the
application, and can't be retried once its content has started, until the [hash backfill](providers/sql-server-upgrades.md#recording-hashes-for-older-versions)
records its hash.) Its `CopiedFrom` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> names the version it restored, so
a history view can show it. A restore works in any container that still has the version <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span>. In a
container that keeps a single version, an older version exists only with expiry on, for the five minutes after it's
replaced. (In 3.1,
`RestoreVersionAsync` throws `InvalidOperationException` when versioning is off.)

## Deleting

| Call | Deletes |
|---|---|
| `DeleteResourceIfExistsAsync(name)` | the name and every version of it |
| `DeleteResourceIfExistsAsync(name, versionId)` | that one version; if it was current, the most recently stored remaining version becomes current |
| `DeleteContainerIfExistsAsync(container)` | the container and everything in it |

Each returns `true` if it deleted anything. A delete that would remove a version that's
[retained or under a legal hold](deleting-safely.md) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> throws `ResourceRetainedException` and deletes nothing, unless
the version was protected while a batched delete was already under way; see
[Deleting safely](deleting-safely.md#what-happens-to-a-protected-version).

With SQL Server, deleting a name with many large versions happens in batches that commit one at a time, oldest first
and the current version last, so the name keeps its current version until the end. If the delete is canceled or
fails partway, the versions it reached are gone, every other version is still whole, and calling it again finishes
the job. A version uploaded while the delete runs is newer than the ones it set out to delete, and stays.
