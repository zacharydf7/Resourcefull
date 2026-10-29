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
and the one that's made current last wins. If an application needs "update only if unchanged", it has to check
`VersionId` or `ContentHash` itself, and accept that another upload can land between its check and its write.

Clients are safe to share. `IResourceServiceClient` is registered as a scoped service, and a container client is a
lightweight handle over it, so create them freely per request or per unit of work.
