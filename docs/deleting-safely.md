# Deleting safely

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Some files mustn't be deleted yet: invoices a business has to keep for seven years, contracts until they end, or
whatever a lawsuit needs. Resourcefull can protect a version in two ways:

- **A retention date** (`RetainUntil`): the version can't be deleted until that date has passed.
- **A legal hold** (`LegalHold`): the version can't be deleted until the hold is lifted, whatever its date.

While either applies, nothing deletes the version: not your code, not [expiry](expiry.md), not
[pruning](versioning.md), and not a delete of its container. A refused delete throws `ResourceRetainedException` and
deletes nothing, with one exception, [below](#what-happens-to-a-protected-version).

And for the deletes that do go ahead, a [bin](#the-bin) keeps what was deleted for a time you choose, so a mistake can
be undone.

## Keeping versions for a period

Give an upload a date:

```csharp
await invoices.UploadAsync("2026/inv-1042.pdf", stream, new ResourceUploadOptions
{
    RetainUntil = new DateTimeOffset(2033, 12, 31, 0, 0, 0, TimeSpan.Zero)
});
```

Or let the container's [policy](container-policies.md) give every new version one, counted from when its write starts:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseContainerPolicies(new ResourceContainerPolicy { RetainFor = TimeSpan.FromDays(7 * 365) }));
```

A policy provider can set `RetainFor` for the containers that need it, such as the invoices container, and leave
the rest alone. An upload's own `RetainUntil` takes the place of the policy's. Copies and restores get the policy's
date too, not their source's; `ResourceCopyOptions.RetainUntil` gives a copy its own.

A date can be moved later, never earlier, and can't be removed:

```csharp
await invoices.SetRetentionAsync("2026/inv-1042.pdf", versionId: null, new DateTimeOffset(2035, 12, 31, 0, 0, 0, TimeSpan.Zero));
```

An earlier date throws `ArgumentOutOfRangeException` and changes nothing. That's on purpose: if a date could be moved
earlier, a mistake or someone acting in bad faith could undo the protection.

## Legal holds

A hold keeps a version until it's lifted:

```csharp
await contracts.SetLegalHoldAsync("acme/msa.pdf", versionId: null, hold: true);
// ... once the matter is closed:
await contracts.SetLegalHoldAsync("acme/msa.pdf", versionId: null, hold: false);
```

An upload or copy can also start out held, with `LegalHold = true` in its options.

## What happens to a protected version

- **Deleting it, its name or its container** throws `ResourceRetainedException`, which names the version and says
  why: its `RetainUntil`, its `LegalHold`, or both. Nothing is deleted, not even the name's other versions or the
  container's other resources. The [ASP.NET Core exception handler](aspnet-core/status-codes.md) answers it with 409.
  The one exception: SQL Server, PostgreSQL and SQLite delete a container, or a name with many large versions, in
  batches. If a hold or a later date lands on one of its versions while such a delete is already under way, the delete
  stops at that version, and the versions it reached first stay deleted.
- **Expiry** leaves it. A protected version that has expired is deleted by the first sweep after its retention date
  has passed and its hold is lifted.
- **Pruning** keeps it past the container's `MaxVersions`. An upload that replaces it still succeeds, and the
  protected version stays on as an older version. Once it's no longer protected, the next prune removes it: the next
  upload of that name, or the container client's `PruneAsync`.
- **In a container that keeps one version,** the current version can't be deleted on its own while a version it
  replaced is protected, since that version would become current again; delete the whole name once nothing in it is
  protected. Expiry leaves such a current version too, until then. The check runs just before the delete, so a hold
  set on a replaced version between the check and the delete can still let that version become current.
- **Moving or renaming it** takes its date and hold along with it, so it's just as protected under its new name.
- **Its metadata and content type** can still change. Its content and hash never can, protected or not.
- **Exporting a container** keeps each version's date and hold, and importing the export restores them.

`ResourceInfo.RetainUntil` and `ResourceInfo.LegalHold` say what protects a version, and
`ResourceInfo.IsDeletableAt(now)` whether it can be deleted at that time.

## Who may protect and release

Resourcefull doesn't decide who may set a date or lift a hold: check that in your application before calling
`SetRetentionAsync` or `SetLegalHoldAsync`, as you would for any other change. Since a date can't be moved earlier, one
set too far ahead can't be undone. Check dates before you set them, especially in a policy.

Every refused delete is counted in the `resourcefull.client.delete.refused` metric, tagged with what refused it and
why; see [Telemetry](telemetry.md).

## The bin

With a bin, a delete doesn't remove a version straight away. It goes to its container's bin, where you can bring it
back, and it's purged for good once the time you chose has passed. Turn it on with the container
[policy](container-policies.md)'s `KeepDeletedFor`, and turn on [expiry](expiry.md), whose sweep does the purging:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseExpiry()
    .UseContainerPolicies(new ResourceContainerPolicy { KeepDeletedFor = TimeSpan.FromDays(30) }));
```

As with any policy property, a policy provider can give each container its own: a bin for documents people edit, none
for scratch space. Without `UseExpiry`, nothing would ever purge the bin, so a default policy that sets
`KeepDeletedFor` stops the application at startup. A policy provider's answer can't be checked then, so a delete in a
container whose policy keeps deleted versions throws `InvalidOperationException` instead, and deletes nothing.

### What goes to the bin

- **Deletes.** `DeleteResourceIfExistsAsync` puts the versions it deletes in the bin, whether that's a whole name or
  one version. With a [condition](concurrency.md), the condition is checked as for any delete.
- **Pruned versions,** past the container's [`MaxVersions`](versioning.md#keeping-a-limited-number), as soon as
  they're pruned. They skip the five minutes pruning otherwise gives them, since a download already reading one can
  finish while it's in the bin. Your [cleanup handler](expiry.md#auditing-whats-deleted) hears about each one as
  `Pruned`.
- **Not expired versions.** An expiry is often set to erase data on time, so expiry deletes for good.
- **Not container deletes.** Deleting a container deletes its bin too.
- **Not refused uploads.** A version an [upload inspector](upload-inspection.md) refuses was never stored, so it
  doesn't go to the bin, and neither does an [upload session](uploading-in-parts.md) that's canceled.

A version that's retained or held can't be deleted at all, bin or not: the delete is refused, as above.

### While a version is in the bin

- **It's gone from every read:** downloads, `ExistsAsync`, listings, version lists and
  [searches by metadata](listing.md#finding-resources-by-metadata). A download already reading it can finish.
- **Its name is free.** Once every version of a name is in the bin, a create-only upload of the name succeeds, and
  the new version has nothing to do with the deleted ones. A version ID in the bin stays taken, so an
  [import](backup-and-move.md) of a version with that ID is refused.
- **It doesn't count toward the container's limits.** `MaxContainerSize` and `MaxResourceCount` leave the bin out, so
  deleting makes room. `GetUsageAsync` reports the bin apart, as `DeletedSize` and `DeletedVersionCount`, and the
  [dashboard](aspnet-core/dashboard.md) shows them as "In the bin".
- **Expiry leaves it alone.** It keeps its `ExpiresAt`, and if that has passed when the version comes back, the next
  sweep deletes it.
- **A move or rename leaves the bin behind.** Only the versions outside the bin move; the bin stays with the old name.
  A name with only a bin can be the destination of a move, and keeps its bin.
- **An export leaves the bin out.**

### Listing the bin

`ListDeletedAsync` lists a container's bin, newest deletion first, with `ResourceInfo.DeletedAt` and `PurgeAt` set
on each version:

```csharp
await foreach (ResourceInfo deleted in documents.ListDeletedAsync(new ResourceListOptions { Prefix = "reports/" }))
{
    Console.WriteLine($"{deleted.Name} ({deleted.VersionId}): deleted {deleted.DeletedAt}, purged after {deleted.PurgeAt}");
}
```

`GetDeletedPageAsync` gets one page at a time, for an API that pages its own callers, as `GetResourcePageAsync` does.
With a prefix, a page can hold fewer versions than its page size, or none, while more follow, so keep going until
`ContinuationToken` is `null`. A listing of the bin can't match metadata: setting `Metadata` throws `ArgumentException`.

### Bringing a version back

```csharp
await documents.UndeleteAsync("reports/q3.xlsx");                        // every version of the name in the bin
await documents.UndeleteAsync("reports/q3.xlsx", deleted.VersionId);     // one of them
```

Each version comes back as an older version of its name, so the name's current version stays current. If the name has
no current version, as when the whole name was deleted, the most recently stored version that came back becomes
current. To make an older version current, [restore](versioning.md) it, as you would any older version.

Since the bin doesn't count toward the container's limits, bringing versions back could put the container over them, so
`UndeleteAsync` is refused with `ResourcePolicyViolationException`, and brings nothing back, when the container has no room for their
content under `MaxContainerSize`, or no room for the name under `MaxResourceCount`. A read-only container refuses it too.

### Purging

A version in the bin is purged at its `PurgeAt`: when it was deleted, plus the `KeepDeletedFor` in force then.
Changing `KeepDeletedFor` applies to later deletes only. The expiry sweep purges what's due within one interval,
telling your cleanup handler about each version first, as `Purged`. A read-only container keeps its bin until it's
writable again.

To purge before then, such as to erase personal data straight away, call `PurgeDeletedAsync`:

```csharp
await documents.PurgeDeletedAsync("reports/q3.xlsx");                    // every version of the name in the bin
await documents.PurgeDeletedAsync("reports/q3.xlsx", deleted.VersionId); // one of them
```

That's your own delete, so the cleanup handler isn't told about it. `IResourceServiceClient.PurgeDueAsync()` runs the
sweep's purge whenever you call it, so it purges only versions whose `PurgeAt` has passed.

### An example: invoices

An invoices container keeps every invoice for seven years, and keeps deleted ones for 30 days after that:

```csharp
public sealed class InvoicePolicies : IResourcePolicyProvider
{
    public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ResourceContainerPolicy?>(containerName == "invoices"
            ? new ResourceContainerPolicy { RetainFor = TimeSpan.FromDays(7 * 365), KeepDeletedFor = TimeSpan.FromDays(30) }
            : null);
}
```

- **In the first seven years,** deleting an invoice throws `ResourceRetainedException`, and nothing changes.
- **After that,** deleting it puts it in the bin. For 30 days, `UndeleteAsync` brings it back, if it was deleted by
  mistake.
- **After those 30 days,** the sweep purges it for good, and your cleanup handler records that.
- **If a court asks for it** while it's still retained, a legal hold keeps it past its seven years until the hold is
  lifted, and only then can it go to the bin.

### Telemetry

`ListDeletedAsync` and `GetDeletedPageAsync` record `list_deleted`, `UndeleteAsync` records `undelete`, and
`PurgeDeletedAsync` records `purge_deleted`. A sweep that finds versions due records `purge`, with
`resourcefull.purged`. Counters add up the versions purged (`resourcefull.client.purged`), those a sweep failed to
purge (`resourcefull.client.purge.failures`) and those brought back (`resourcefull.client.undeleted`); see
[Telemetry](telemetry.md).

## With each provider

Every provider supports retention and legal holds, and checks them in the same step as the delete, so a hold set at the
same moment as a delete either stops it or finds the version already gone. Every provider has the bin too.

- **SQL Server, PostgreSQL and SQLite** keep both on the version's row. A container delete checks for protected
  versions first, through an index of only those, so a container without any pays almost nothing.
- **The file system, S3 and Azure Blob** keep both with the version, and write a small entry for each version
  that's ever been protected. A container delete reads those entries to check the versions they name. A write that
  protects a version while that container is being deleted waits for the delete to decide, at most a minute, and then
  either succeeds or finds the container gone.
- **A provider of your own** reports whether it supports them; see
  [Writing a provider](providers/writing-a-provider.md#retention-and-legal-holds). With one that doesn't, a write that
  would protect a version throws `NotSupportedException`, so nothing is stored unprotected.

For the bin:

- **SQL Server, PostgreSQL and SQLite** keep a version in the bin as its row, marked deleted, which every other query
  leaves out. Putting versions in the bin, or bringing them back, is one transaction, however many versions the name
  has. Two small indexes, of only the rows in bins, serve the bin's listings and the sweep, so a sweep with nothing to
  purge reads almost nothing. Purging deletes in batches, as any delete of many versions does.
- **The file system, S3 and Azure Blob** keep a version in the bin with its name's other versions, so putting it there
  or bringing it back is one write, and leave its content where it was. Each version in a bin also has two small
  entries: one in its container's bin, which listings and the usage figures read, and one by purge time, which the
  sweep reads. A listing reads each entry and its name's versions, so listing a large bin costs more than with a
  database; with a prefix, a page stops after looking at 1,000 entries. The usage figures add up the bin from the
  entries' names alone.
- **A provider of your own** reports whether it has a bin; see
  [Writing a provider](providers/writing-a-provider.md#the-bin). With one that doesn't, a delete in a container whose
  policy keeps deleted versions throws `NotSupportedException` and deletes nothing.
