# Deleting safely

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Some files mustn't be deleted yet: invoices a business has to keep for seven years, contracts until they end, or
whatever a lawsuit needs. Resourcefull can protect a version in two ways:

- **A retention date** (`RetainUntil`): the version can't be deleted until that date has passed.
- **A legal hold** (`LegalHold`): the version can't be deleted until the hold is lifted, whatever its date.

While either applies, nothing deletes the version: not your code, not [expiry](expiry.md), not
[pruning](versioning.md), and not a delete of its container. A refused delete throws `ResourceRetainedException` and
deletes nothing, with one exception, [below](#what-happens-to-a-protected-version).

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
  upload of that name, or `PruneContainerAsync`.
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

## With each provider

Every provider supports retention and legal holds, and checks them in the same step as the delete, so a hold set at the
same moment as a delete either stops it or finds the version already gone.

- **SQL Server, PostgreSQL and SQLite** keep both on the version's row. A container delete checks for protected
  versions first, through an index of only those, so a container without any pays almost nothing.
- **The file system, S3 and Azure Blob** keep both with the version, and write a small entry for each version
  that's ever been protected. A container delete reads those entries to check the versions they name. A write that
  protects a version while that container is being deleted waits for the delete to decide, at most a minute, and then
  either succeeds or finds the container gone.
- **A provider of your own** reports whether it supports them; see
  [Writing a provider](providers/writing-a-provider.md#retention-and-legal-holds). With one that doesn't, a write that
  would protect a version throws `NotSupportedException`, so nothing is stored unprotected.
