# Moving to another provider

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Your code talks to Resourcefull's clients, never to the storage behind them, so switching from SQL Server to the file
system, or from a database to S3 or Azure Blob Storage, is a change of one line in your setup. What has to move is the
content already stored. Resourcefull copies it for you, store to store, version by version, without a file in between,
and checks the result.

This page walks through a move from start to finish. It works between any two built-in providers, in either direction,
and between two stores of the same provider, such as an old database and a new one.

## What a copy keeps

A copy keeps every version of every file, not only the current ones, exactly as it was:

- **Version IDs and `LastModified`,** so anything that recorded a version ID, such as a [signed link](sharing-links.md)
  or a row in your own database, still finds it.
- **Content types, metadata, [`CopiedFrom`](copying-and-moving.md), [expiries](expiry.md),
  [retention dates and legal holds](deleting-safely.md),** and the order of each file's versions, so the same version
  is current.
- **Each container's metadata.**

Each version's content is checked against its hash as it's stored, so content damaged on the way is refused rather
than copied wrong.

What a copy leaves behind:

- **[Upload sessions](uploading-in-parts.md) still in progress.** Let them finish, or have their uploaders start
  again on the new store.
- **The [bin](deleting-safely.md#the-bin).** Deleted versions stay in the old store. Bring back anything you want to
  keep before the move, or purge the bin if you don't need it.
- **[Container policies](container-policies.md).** They live in your code, not in storage, so register the same ones
  for the new store.

## Copying one container

```csharp
IResourceServiceClient oldStore = services.GetRequiredService<IResourceServiceClient>();
IResourceServiceClient newStore = services.GetRequiredKeyedService<IResourceServiceClient>("new");

ContainerCopyResult copied = await oldStore.CopyContainerToAsync("invoices", newStore, cancellationToken: cancellationToken);
Console.WriteLine($"{copied.VersionCount} versions, {copied.ContentSize}");
```

- **By default, the destination container mustn't exist.** `Existing` in `ContainerCopyOptions` changes that, as it
  does for [a whole store](#copying-a-whole-store). `DestinationName` gives the copy another name, which also lets you
  copy a container within one store. This sets both:

  ```csharp
  ContainerCopyResult result = await oldStore.CopyContainerToAsync("invoices", newStore, new ContainerCopyOptions
  {
      DestinationName = "invoices-2026",
      Existing = ExistingContainers.Update
  }, cancellationToken);
  ```

  `Outcome` says what happened: `Copied`, `Updated` or `Matching`. `VersionCount` and the other counts say what was
  copied, and when the container was brought up to date, `Update` says what changed in it.
- **A copy that fails deletes a container it created,** as an [import](backup-and-move.md#imports-are-checked-and-all-or-nothing)
  does, so you can fix the cause and copy again. While it fills, the destination's container is visible, but marked
  unfinished in its metadata.
- **The destination's rules apply:** its policies and [upload inspector](upload-inspection.md) see every version, as
  for an import.
- **The source is only read,** a page of files at a time. A version stored after its file was read isn't copied;
  the catch-up below deals with that.

## Copying a whole store

`CopyStoreToAsync` copies every container, one at a time, each the way `CopyContainerToAsync` does:

```csharp
StoreCopyResult result = await oldStore.CopyStoreToAsync(newStore, new StoreCopyOptions
{
    Prefix = null,                                // every container, or only those whose names start with this
    Existing = ExistingContainers.Fail,           // what to do with a container the new store already has
    OnFailure = StoreCopyFailures.Stop,           // stop at the first failure, or go on
    Progress = new Progress<ContainerCopied>(c => logger.LogInformation("{Name}: {Outcome}", c.Name, c.Outcome))
}, cancellationToken);
```

`Existing` decides what happens to a container the new store already has:

| `Existing` | A container the new store already has |
|---|---|
| `Fail` (the default) | fails, and is left as it is |
| `SkipMatching` | is skipped if it matches the old store's version for version, and fails otherwise, naming the first differences |
| `Update` | is brought up to date in place: versions it lacks are imported, changed attributes are applied, and versions the old store no longer has are deleted |

**Matching means version for version.** Two containers match when they have the same files, each with the same
versions in the same order, and each version has the same ID, content hash and length, content type, metadata,
`LastModified`, `CopiedFrom`, expiry, retention date and hold, and the containers have the same metadata. Totals such
as counts and sizes are never trusted on their own. Times are compared to the microsecond, the finest every provider
keeps. Comparing reads every file's list of versions on both sides, but no content, so it costs far less than copying.

**`Update` never deletes a container.** If it fails partway, what it did stays, and running it again goes on from
there. Before it changes a file, it refuses anything it can't do in place:

- a version the new store lacks that's older than versions it has, as after an
  [undelete](deleting-safely.md#bringing-a-version-back) in the old store, since an import adds each version as the
  newest;
- versions both stores have that are in another order;
- a version whose content, `LastModified` or `CopiedFrom` differs, since none of those can change in place;
- a version the new store retains for longer, since a retention date can't move earlier.

Copy such a container afresh: delete it in the new store, if nothing in it is retained or held, and copy it again.
`Update` never bypasses a retention date or a hold: deleting a version the old store no longer has fails if the new
store protects it.

**A container another copy is still filling fails,** whatever `Existing` says, so two copies don't work on one
container. A copy marks the container it fills, and writes the mark again every five minutes, so a container whose
mark is more than half an hour old belongs to a copy that stopped. `Update` finishes it, and `SkipMatching` and `Fail`
fail it.

**`OnFailure` decides what a failure does.** With `Stop`, the default, the first container that fails stops the copy
with a `StoreCopyException`, whose `Result` says what the copy did before. With `Continue`, the copy goes on, and the
result lists every failure. Canceling the copy throws `OperationCanceledException` either way.

**The result** lists the containers copied, those updated with what changed in each, those that matched, those that
failed with why, and the new store's containers within the prefix that the old store doesn't have. `Succeeded` is
`true` only when nothing failed.

## Switching over

A move that loses nothing copies while your application runs, then stops writes only for the catch-up, which copies
just what changed but still reads every file's list of versions on both sides.

1. **Register both stores,** the new one with the same policies as the old:

   ```csharp
   builder.Services
       .AddResourcefull(options => options.UseSqlServer(oldConnection).UseContainerPolicies(policy))
       .AddKeyedResourcefull("new", options => options.UseFileSystem(newPath).UseContainerPolicies(policy));
   ```

2. **Copy while the application runs.** This takes as long as the content takes to move, and nothing waits for it.

   ```csharp
   StoreCopyResult first = await oldStore.CopyStoreToAsync(newStore, new StoreCopyOptions { OnFailure = StoreCopyFailures.Continue });
   ```

   Look at `first.Failed`, fix what made those containers fail, and run it again with `Existing = Update` until
   nothing fails.

3. **Stop every write to the old store.**
   - Give the old store a default policy of `ReadOnly = true` on every instance of the application, and on every
     background worker. A read-only container refuses every change, and the [expiry](expiry.md) sweep leaves it
     alone, so nothing changes in it.
   - Wait for requests and [upload sessions](uploading-in-parts.md) that started before then to finish.
   - Stop anything else that writes to the old store, such as maintenance jobs.

4. **Catch up.** Copy the changes made since the first copy:

   ```csharp
   await oldStore.CopyStoreToAsync(newStore, new StoreCopyOptions { Existing = ExistingContainers.Update });
   ```

5. **Check.** Compare every container, version for version:

   ```csharp
   StoreCopyResult check = await oldStore.CopyStoreToAsync(newStore, new StoreCopyOptions { Existing = ExistingContainers.SkipMatching });
   ```

   With nothing failed, the new store holds exactly what the old one does. Look at `check.DestinationOnly` too, for
   containers the new store has that the old one doesn't. `GetUsageAsync` on both sides makes a quick sanity check,
   but it's the comparison that proves the copy.

6. **Switch.** Point your default store at the new provider, deploy, and leave the old store read-only.

7. **Keep the old store until you're sure.** Nothing deletes it. Until the new store takes its first write, switching
   back to the old one loses nothing. After that, switching back loses what was written to the new store, unless you
   first copy it back the same way, with `Update` from the new store to the old.

## How long it takes

A copy costs about what an [export](backup-and-move.md) of the old store plus an import into the new one would, without
the disk space for the zip: each version's content is read once from the old store and written once to the new.
Containers are copied one at a time, and a container's files a page of 1,000 at a time. A check with `SkipMatching`
reads each file's list of versions on both sides, and no content.

## With a provider of your own

A provider of your own can be either side. As the old store, it needs nothing more than reads. As the new store, it
needs the provider method `ImportVersionAsync`, as [imports](backup-and-move.md#custom-providers) do. More generally,
the destination can be any `IResourceServiceClient` whose `ImportContainerAsync` accepts a stream of versions, and whose
`ImportVersionsAsync` works for `Update`.

## Telemetry

Each container copy records a `copy_container` operation, with `resourcefull.resources` and `resourcefull.versions`,
and the new store records its own `import` inside it. A store copy records `copy_store`, with how many containers it
copied, updated, found matching and failed. The new store counts the bytes it stores in `resourcefull.client.uploaded`,
as an import does, and `resourcefull.client.copy.versions` counts the versions copies imported, changed and deleted, by
`resourcefull.copy_action`. See [Telemetry](telemetry.md).
