# Concurrency

Resourcefull is built to be called from many requests, processes and servers at once. Both built-in providers make the
same guarantees:

- **One current version per name, always.** Uploads of the same name are made current one at a time, so however
  many run at once, each name ends up with exactly one current version: the one stored last. SQL Server also enforces
  this with a unique index.
- **Uploads don't wait for each other's content.** Each upload streams its content on its own, and the name is locked
  only for the moment the new version is made current.
- **Create-only uploads are decided by the write.** Of several concurrent uploads of a new name with `CreateOnly` set
  (or, without versioning, without `Overwrite`), exactly one succeeds and the rest throw `ResourceConflictException`.
  There's no window between a check and the write.
- **Creating a container is decided by the write too.** Of several concurrent `CreateResourceContainerAsync` calls for
  one name, exactly one succeeds. `CreateResourceContainerIfNotExistsAsync` returns a client to every caller.
- **An upload never succeeds into a missing container.** A container deleted while an upload into it runs makes the
  upload throw `ResourceContainerNotFoundException`, not report success.
- **Pruning never leaves more than `MaxVersions`**, even with concurrent uploads, because each prune works from the
  versions as they stand after its own write. A prune that fails leaves the extra versions until a later upload of the
  name prunes them (see [Versioning](versioning.md#keeping-a-limited-number)).
- **A download reads the version it resolved.** If another upload makes a newer version current while you read, you
  keep reading the version the `ResourceInfo` describes.
- **Readers don't block writers.** With SQL Server, reads use row versions
  ([`READ_COMMITTED_SNAPSHOT`](providers/sql-server.md#read_committed_snapshot)) instead of shared locks, and
  deadlocks that contention still causes are retried for you.

What isn't coordinated is the order of independent operations. Two overwriting uploads that run at once both succeed,
and the one that's made current last wins.

## Updating only if unchanged

When a client reads a resource, changes it and uploads it again, another client can do the same in between, and one
of the two changes is silently lost. Make the upload conditional on the version you read:

```csharp
ResourceInfo read = (await container.GetResourceInfoAsync("report.docx"))!;
// ... edit ...
await container.UploadAsync("report.docx", edited, new ResourceUploadOptions
{
    Conditions = new ResourceConditions { IfMatchVersionId = read.VersionId }
});
```

The condition is checked by the write itself, at the moment it would make the new version current, so of several
clients that read the same version, exactly one succeeds. The others get `ResourcePreconditionFailedException`, which
carries the version that's current now (`CurrentVersionId` and `CurrentContentHash`), and nothing of theirs is stored.
They can read again, reapply their change and retry.

- **`IfMatchContentHash`** compares the content hash instead. [Serving downloads](aspnet-core/serving-downloads.md)
  sends it as the `ETag`, in quotes as HTTP requires, so pass the tag from an `If-Match` header without its quotes.
  `UploadFromRequestAsync` in the [ASP.NET Core package](aspnet-core/accepting-uploads.md) does that for you,
  along with `*`, weak tags and lists of tags. Versions stored before 3.0 have no hash until the
  [backfill](providers/sql-server-upgrades.md#recording-hashes-for-older-versions) records one, and until then they meet no
  hash condition.
- **Set both** and both must hold.
- **A name with no current version meets no condition.** For "only if it doesn't exist yet", use `CreateOnly`; it
  can't be combined with conditions.
- **A conditional upload replaces the version it matched**, even without versioning or `Overwrite`.
- **Deletes take conditions too**, with
  `DeleteResourceIfExistsAsync(name, versionId, conditions, cancellationToken)`. The conditions apply to the name's
  current version, and a name that doesn't exist throws rather than returning `false`.

Clients are safe to share. `IResourceServiceClient` is registered as a scoped service, and a container client is a
lightweight handle over it, so create them freely per request or per unit of work.
