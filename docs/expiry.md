# Expiring resources

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

Uploads can delete themselves: exports, previews, temporary shares. Give an upload an expiry, and turn on the
background service that deletes what has expired:

```csharp
builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseExpiry());   // looks for expired versions every minute; pass a TimeSpan to change that

await container.UploadAsync("exports/report.zip", stream, new ResourceUploadOptions
{
    ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
});
```

## How it works

- **The expiry belongs to the version,** and `ResourceInfo.ExpiresAt` reports it. An expired version is deleted as a
  delete of that version would delete it: a name whose only version expires is gone, and when the current version
  expires, with [versioning](versioning.md#keeping-a-limited-number), the one stored before it becomes current again.
  Without versioning, the versions it replaced go with it.
- **A version is gone within one interval of expiring.** Until the service gets to it, it can still be read. Any time
  works, from seconds to years away, or in the past, which the next sweep deletes. For expiries of a few seconds, use a
  shorter interval, such as `UseExpiry(TimeSpan.FromSeconds(5))`: with nothing expired, a sweep reads one row of the
  index of versions that expire.
- **Every instance of the application can run it.** Deleting a version takes its name's lock, so instances don't
  conflict, and a version two instances find is deleted once.
- **One version that fails doesn't hold up the rest.** If a version can't be deleted, the sweep carries on with the
  others and reports the failure; that version is tried again next time.
- **Read-only containers don't expire.** A [read-only](container-policies.md) container keeps its expired versions,
  unreported, until it's writable again, and its expiries can't be changed. Freezing a container keeps everything
  in it. Each sweep still goes past the expired versions it keeps, a seek on that index for each batch of 100.
- **It also deletes what stopped uploads left behind** <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, with SQL Server: the
  [staged content](providers/sql-server-operations.md#uploads-connections-and-the-log) of an upload whose process
  died partway, an hour after that upload last stored anything.
- **Without `UseExpiry`,** expiries are stored, and `IResourceServiceClient.DeleteExpiredAsync()` deletes what has
  expired, and what stopped uploads left behind, whenever you call it, such as from a scheduled job.

## Changing an expiry

`SetExpiryAsync` moves a version's expiry, such as to extend a share, or with `null` keeps it for good:

```csharp
await container.SetExpiryAsync("exports/report.zip", versionId, DateTimeOffset.UtcNow.AddDays(7));
```

Only the expiry changes: the content, `LastModified` and ETag stay as they are. It returns `false` when the version is
gone, as it is if the service deleted it first. A [read-only](container-policies.md) container refuses it.

## Copies, moves and restores

- **A copy** doesn't expire unless `ResourceCopyOptions.ExpiresAt` says so.
- **A move** keeps each version's expiry.
- **A restore** doesn't expire: it brings back a version to keep.

## Auditing what's deleted

To record every version Resourcefull deletes by itself, such as in an audit system, register a cleanup handler. It
hears about expired versions, and about older versions [pruned](versioning.md#keeping-a-limited-number) past
`MaxVersions`, before each is deleted. With expiry on, pruning gives a version five minutes before the sweep deletes
it, so the handler hears about it then, with the reason `Expired`, or with `Pruned` if, without versioning, it's
deleted sooner along with the version that replaced it:

```csharp
public sealed class AuditCleanup(AuditClient audit) : IResourceCleanupHandler
{
    public async ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken) =>
        await audit.RecordAsync(cleanup.Resource.ContainerName, cleanup.Resource.Name, cleanup.Resource.VersionId,
            cleanup.Reason, cleanup.At, cancellationToken);
}

builder.Services.AddResourcefull(options => options.UseSqlServer(connectionString).UseExpiry()
    .UseCleanupHandler<AuditCleanup>());
```

- **No deletion goes unreported.** A version is deleted only after the handler returns. If it throws, as when the
  audit system is down, the version stays, and the next sweep, or the name's next prune, tries again.
- **A report means "about to be deleted",** not "deleted". A version can be reported more than once, by each instance
  or prune that finds it, or again when its delete failed, and occasionally it isn't deleted after all, when its
  expiry was extended or something else deleted it first. Record reports by version ID to ignore repeats, and check
  `ExistsAsync` if you need to know it's gone.
- **Your own deletes aren't reported:** audit those where your code makes them.
- **It's a scoped service,** so it can use your other services.

## With a provider of your own

Expiry needs a provider that stores each version's `ExpiresAt` and can list and delete expired versions. Both built-in
providers do. With a [provider of your own](providers/writing-a-provider.md) written for 3.1 or earlier, the expiry
service logs a warning that the provider doesn't support it and stops. More importantly, uploads with an expiry are
still accepted, but the provider drops the expiry, so those versions are never deleted. Check that your provider stores
`ExpiresAt` before relying on expiry, for example by uploading with an expiry and reading `ResourceInfo.ExpiresAt`
back.

## Telemetry

Each sweep that finds expired versions records an `expire` operation, with `resourcefull.expired` counting the ones
it deleted, and `resourcefull.abandoned_uploads` the stopped uploads whose leftovers it deleted, and a sweep that
finds no expired versions records nothing. A read-only container's expired versions count as found, so
while one keeps some, every sweep records an operation, deleting none of those. `SetExpiryAsync` records `set_expiry`.
Counters add up the versions sweeps delete (`resourcefull.client.expired`) and fail to delete
(`resourcefull.client.expire.failures`), and the cleanup handler's failures; see
[Work that happens out of sight](telemetry.md#work-that-happens-out-of-sight).
