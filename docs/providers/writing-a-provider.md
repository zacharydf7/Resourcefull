# Writing a provider

To store resources somewhere else, such as a file system, object storage or another database, implement
`IResourceRepository` and register it with `UseProvider`. The clients handle names, versioning, pruning, URIs and
cancellation plumbing; a provider stores and retrieves.

```csharp
public static class ResourcefullFileSystemOptionsExtensions
{
    public static ResourcefullOptionsBuilder UseFileSystem(this ResourcefullOptionsBuilder builder, string root)
    {
        builder.Services.AddSingleton(new FileSystemOptions(root));
        return builder.UseProvider<FileSystemRepository>(ServiceLifetime.Singleton);
    }
}
```

`IResourceRepository` follows semantic versioning: it changes incompatibly only in a major release of `Resourcefull`,
so a provider package can depend on a version range such as `[3.0.0, 4.0.0)`.

## The contract

The XML documentation on each member states its contract in full. In short, a provider must:

- **Treat names and version IDs as untrusted.** The clients only guarantee that names follow the
  [name rules](../containers-and-names.md#names); `/`, `\`, `..` and any other characters can still arrive, and
  version IDs on reads and deletes aren't checked at all. Encode or escape them before using them as file paths or
  keys.
- **Compare names and version IDs ordinally** (case-sensitive), and size storage for
  `ResourceNames.MaxContainerNameLength`, `MaxResourceNameLength` and `MaxVersionIdLength`. These limits change only
  in a major release.
- **Keep exactly one current version per name.** The current version is the one stored most recently, not the one
  with the latest `LastModified`, and concurrent writes and deletes must never leave two.
- **List versions in store order:** the current version first, then from the most recently stored to the oldest.
  Pruning keeps the first `MaxVersions` and deletes the rest, so another order deletes the wrong versions.
- **Decide conflicts in the write itself:** a missing container throws `ResourceContainerNotFoundException`, and a
  create-only write to an existing name or a duplicate container throws `ResourceConflictException`. A check before
  the write leaves a window for a concurrent caller.
- **Measure what it stores.** Read the content through a `HashingStream`, store the length it measured, and return
  `HashingStream.Complete(declaredLength, lastModified)`, which also rejects a wrong declared length. Call it as the
  write makes the version current but before it commits, so a wrong length stores nothing.
- **Stamp the time a version becomes current.** Pass `Complete` the time the write makes the version current, not the
  time before its content was read, and store it as the version's `LastModified`. Ignore the `LastModified` the caller
  passes. Take an optional `TimeProvider` in the constructor, so an application or a test can set the clock.
- **Store the content hash** from `WrittenContent` with the version, and return it as `ResourceInfo.ContentHash` on
  every read.
- **Return content as a read-only, forward-only stream:** `CanSeek` and `CanWrite` are `false`, `Length` is the
  content's full length, `Position` counts the bytes read, and seeking, setting `Position`, writing and `SetLength`
  throw `NotSupportedException`.
- **Store container metadata**, and return copies, so callers can't change what's stored. Storing `Uri` is optional:
  the clients replace it with the URI provider's whenever they return a container or version.
- **Honor cancellation:** a cancelled operation throws `OperationCanceledException` and leaves nothing half-done. The
  one exception is deleting a container, which may stop between whole versions, as long as each remaining name keeps a
  current version and calling it again finishes the job.
- **Be safe to call concurrently** for the lifetime it registers with.

### Listing by page

`GetResourcePageAsync` was also added in 3.1. Its default implementation reads the whole container through
`GetAllResourceInfoAsync` and sorts and filters it in memory, so a provider written for 3.0 lists correctly, just
without the savings. To answer it directly, return at most `pageSize` current versions whose names start with
`prefix`, ordinally, and come after `afterName`, ordered by name. Use ordinal order, or your storage's binary order
if its ordering and its "after" comparison agree, so no name is skipped or repeated between pages.

### Reading ranges

The overload of `GetResourceStreamAsync` that takes an `offset` and a `length` was added in 3.2. Its default
implementation opens the whole content and reads past the bytes before `offset`, so a provider written for 3.1
returns correct ranges, just without the savings. To answer it directly, return a stream that starts at `offset`:

- **`Length` is still the content's full length,** so the client can report it, and `Position` starts at `offset`, or
  at the content's length when `offset` is past it.
- **Reading may go on past the range.** The client reads only `length` bytes, so a provider can stop reading storage
  there or carry on to the end, whichever is simpler.

### Copying and moving

`CopyResourceAsync` and `MoveResourceAsync` were added in 3.2, and `AddResourceAsync` should store
`ResourceInfo.CopiedFrom` with the version and return it on every read, as it does the content hash.

- **Copy's default implementation** reads the source and stores it through `AddResourceAsync`, so a provider written
  for 3.1 copies correctly, just through the application, and without `CopiedFrom` unless it stores it. Override it
  to copy within your store, deciding create-only writes and conditions at the destination as an upload does.
- **Move's default throws `NotSupportedException`.** A move must change every version's container and name at once,
  never leaving the resource in both places or half in each, so made of copies and deletes it could stop halfway.
  Implement it to keep each version's ID and attributes, refuse a destination name that has versions
  (`ResourceConflictException`) or whose container is missing (`ResourceContainerNotFoundException`), and decide the
  source conditions in the move itself.

### Expiry

`GetExpiredAsync`, `DeleteExpiredVersionAsync` and `SetExpiryAsync` were added in 3.2, and `AddResourceAsync` and
`CopyResourceAsync` should store `ResourceInfo.ExpiresAt` with the version and return it on every read. Their default
implementations throw `NotSupportedException`, so [expiry](../expiry.md) logs a warning and stops with a provider
written for 3.1. To support it:

- **List expired versions from an index, a batch at a time.** Expiry calls `GetExpiredAsync` every interval on every
  instance, so it should cost little when nothing has expired. Order them by `ExpiresAt`, then ordinally by version
  ID, and start after the `after` version you're given: a sweep pages through every expired version this way, and
  fails if a provider lists the same batch twice.
- **Decide expiry in the delete.** `DeleteExpiredVersionAsync` deletes a version only if it's still expired when the
  delete runs, under whatever serializes writes to the name: `SetExpiryAsync` may have extended it since it was listed.
- **Change nothing but the expiry** in `SetExpiryAsync`.

### Container usage

`GetContainerUsageAsync` was also added in 3.2. Its default implementation lists every resource's versions, one name
at a time, and adds up their `ContentLength`, so a provider written for 3.1 reports usage correctly, just slowly for
large containers. [Container policies](../container-policies.md) call it before each upload they limit by size or
count, so if your store can add up a container's versions in one query, override it. Count every version in
`StoredSize` and `VersionCount`, and each name once in `ResourceCount`.

### Conditional uploads and deletes

The overloads of `AddResourceAsync` and `DeleteResourceAsync` that take conditions were added in 3.1, with default
implementations, so a provider written for 3.0 still compiles. Those defaults throw `NotSupportedException` for an
upload or delete with `ResourceConditions`, so an application using them gets a clear error instead of an unchecked
write. The clients call these overloads only when a caller sets conditions. To support them, implement both overloads
and:

- **Decide the conditions in the write itself,** at the moment the version would become current, or under whatever
  serializes writes to the name, so no other write can come between the check and the change.
- **Treat a name with no current version as meeting no condition,** and empty conditions as none.
- **Throw `ResourcePreconditionFailedException`** with the current version's ID and content hash, and change nothing.

## Testing a provider

The repository's contract test suite, `tests/Resourcefull.Tests.Contract`, is an abstract xUnit class that runs the
same tests against any provider; both built-in providers pass it. To check your provider, copy the suite into your
test project and derive a class that registers your provider:

```csharp
public class FileSystemContractTests : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseFileSystem(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }
}
```
