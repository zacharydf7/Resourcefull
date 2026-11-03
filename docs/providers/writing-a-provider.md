# Writing a provider

To store resources somewhere else, such as a file system, object storage or another database, derive a class from
`ResourceRepository` (in the `Resourcefull.Providers` namespace) and register it with `UseProvider`. The clients handle
names, versioning, pruning, URIs and cancellation plumbing; a provider stores and retrieves. A file that also uses
`System.Reflection` sees two `ResourceLocation` types, so it needs an alias:
`using ResourceLocation = Resourcefull.Providers.ResourceLocation;`.

<span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> This page describes 4.0, where providers derive
from the `ResourceRepository` base class. In 3.1, a provider implements the `IResourceRepository` interface instead;
see [this page as it was for 3.1](https://github.com/zacharydf7/Resourcefull/blob/v3.1.0/docs/providers/writing-a-provider.md),
and [Upgrading to 4.0](../upgrading-to-4.md#custom-providers) for moving a provider across.

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

`ResourceRepository` follows semantic versioning: it changes incompatibly only in a major release of `Resourcefull`,
so a provider package can depend on a version range such as `[4.0.0, 5.0.0)`. A minor release adds members only with
a default implementation, so a provider written for an earlier release keeps compiling.

## The contract

The XML documentation on each member states its contract in full. In short, a provider must:

- **Treat names and version IDs as untrusted.** The clients only guarantee that names follow the
  [name rules](../names.md); `/`, `\`, `..` and any other characters can still arrive, and
  version IDs on reads and deletes aren't checked at all. Encode or escape them before using them as file paths or
  keys.
- **Compare names and version IDs ordinally** (case-sensitive), and size storage for
  `ResourceNames.MaxContainerNameLength`, `MaxResourceNameLength` and `MaxVersionIdLength`. These limits change only
  in a major release.
- **Keep exactly one current version per name.** The current version is the one stored most recently, not the one
  with the latest `LastModified`, and concurrent writes and deletes must never leave two.
- **List versions in store order:** the current version first, then from the most recently stored to the oldest.
  Pruning keeps the first `MaxVersions` and deletes the rest, so another order deletes the wrong versions.
- **Decide conflicts and conditions in the write itself:** a missing container throws
  `ResourceContainerNotFoundException`, and a create-only write to an existing name or a duplicate container throws
  `ResourceConflictException`. A check before the write leaves a window for a concurrent caller. The same goes for
  the conditions an upload or delete is given (`ResourceWriteCondition.Conditions` and `ResourceConditions`): decide
  them at the moment the version would become current, or under whatever serializes writes to the name.
- **Treat a name with no current version as meeting no condition,** and empty conditions as none. A write that fails
  its conditions throws `ResourcePreconditionFailedException` with the current version's ID and content hash, and
  changes nothing.
- **Measure what it stores.** Read the content through a `HashingStream`, store the length it measured, and return
  `HashingStream.Complete(declaredLength, lastModified)`, which also rejects a wrong declared length. Call it as the
  write makes the version current but before it commits, so a wrong length stores nothing.
- **Read the content to its end before the version becomes current,** until a read returns nothing, and store
  nothing if that read throws. That last read waits for an [upload inspector](../upload-inspection.md) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> to
  answer, and throws if it refuses the content, so the version mustn't be current yet. An inspected upload's stream
  can't seek, so a provider can't retry it by reading the content again.
- **Stamp the time a version becomes current.** Pass `Complete` the time the write makes the version current, not the
  time before its content was read, and store it as the version's `LastModified`. Take an optional `TimeProvider` in
  the constructor, so an application or a test can set the clock.
- **Store the content hash** from `WrittenContent` with the version, and return it as `ResourceInfo.ContentHash` on
  every read.
- **Store `ExpiresAt` and `CopiedFrom`** <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> from the `NewResourceVersion` that `AddResourceAsync` and
  `CopyResourceAsync` are given, and return them on every read. Nothing checks that a provider does: one that ignores
  them still accepts uploads with an expiry, and those versions never expire. See [Expiry](#expiry).
- **Return content as a read-only, forward-only stream:** `CanSeek` and `CanWrite` are `false`, `Length` is the
  content's full length, `Position` counts the bytes read, and seeking, setting `Position`, writing and `SetLength`
  throw `NotSupportedException`.
- **Store container metadata**, and return copies, so callers can't change what's stored. Leave `Uri` unset on what
  you return: the clients fill it in from the URI provider.
- **Honor cancellation:** a canceled operation throws `OperationCanceledException` and leaves nothing half-done. The
  one exception is deleting a container, which may stop between whole versions, as long as each remaining name keeps a
  current version and calling it again finishes the job.
- **Be safe to call concurrently** for the lifetime it registers with.

## A skeleton to start from

Ten members are abstract, so these are what a new provider writes first. Each comment says what the member must do;
the contract above and each member's XML documentation say the rest.

```csharp
public sealed class FileSystemRepository(FileSystemOptions options, TimeProvider? timeProvider = null)
    : ResourceRepository
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // Containers

    public override Task AddContainerAsync(string resourceContainerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        // Store the name and a copy of the metadata. A container that already exists throws
        // ResourceConflictException, decided by the write itself, not by a check before it.
        => throw new NotImplementedException();

    public override Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName,
        int pageSize, CancellationToken cancellationToken = default)
        // Up to pageSize containers whose names start with prefix and come after afterName, ordered by name, each
        // with its own copy of the metadata. Seek on the name rather than reading every container.
        => throw new NotImplementedException();

    public override Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        // The container, or null if there's none by that name (compared ordinally).
        => throw new NotImplementedException();

    public override Task<bool> DeleteContainerAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        // The container and every version in it; false if it didn't exist. It may stop between whole versions when
        // canceled, as long as every name left keeps its current version and calling it again finishes.
        => throw new NotImplementedException();

    // Writing

    public override Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
        ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default)
        // Read the content through a HashingStream, store it as a new version, and make that version the name's
        // current one. With condition.CreateOnly, a name that already has a version throws ResourceConflictException;
        // condition.Conditions the current version doesn't meet throw ResourcePreconditionFailedException; a missing
        // container throws ResourceContainerNotFoundException. Return HashingStream.Complete(declared length, the
        // time from _clock as the version becomes current), and store its length and hash with the version.
        => throw new NotImplementedException();

    public override Task<bool> DeleteResourceAsync(ResourceLocation resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default)
        // One version, or every version of the name when versionId is null; false if there was nothing to delete.
        // Deleting the current version makes the next most recently stored one current. Conditions the current
        // version doesn't meet throw ResourcePreconditionFailedException.
        => throw new NotImplementedException();

    // Reading

    public override Task<IReadOnlyList<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        // The current version of every name in the container.
        => throw new NotImplementedException();

    public override Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceLocation resource,
        CancellationToken cancellationToken = default)
        // Every version of the name, the current one first, then from the most recently stored to the oldest.
        => throw new NotImplementedException();

    public override Task<ResourceInfo?> GetResourceInfoAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default)
        // One version, or the current one when versionId is null; null if there's no such version. The version ID
        // comes from the caller unchecked: never let it, or the name, reach outside the name's own storage.
        => throw new NotImplementedException();

    public override Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default)
        // The version's content as a read-only, forward-only stream whose Length is the full length; null if there's
        // no such version.
        => throw new NotImplementedException();
}
```

The rest are virtual, with defaults. Paging, reading a range, copying and container usage work through the members
above, more slowly than a provider that answers them itself. Moving, importing, changing a container's metadata,
changing a version's metadata or content type in place, and `GetExpiredAsync`, `DeleteExpiredVersionAsync` and
`SetExpiryAsync` throw `NotSupportedException` until the provider overrides them, and the features that need them
report that they aren't supported. The sections below say what each default does, and what to do to
override it.

## Listing by page

`GetResourcePageAsync`'s default reads the whole container through `GetAllResourceInfoAsync` and sorts and filters it
in memory, so it lists correctly, just without the savings. To answer it directly, return at most `pageSize` current
versions whose names start with `prefix`, ordinally, and come after `afterName`, ordered by name. Use ordinal order,
or your storage's binary order if its ordering and its "after" comparison agree, so no name is skipped or repeated
between pages.

## Reading ranges

The default of the `GetResourceStreamAsync` overload that takes an `offset` and a `length` opens the whole content and
reads past the bytes before `offset`, so it returns correct ranges, just without the savings. To answer it directly,
return a stream that starts at `offset`:

- **`Length` is still the content's full length,** so the client can report it, and `Position` starts at `offset`, or
  at the content's length when `offset` is past it.
- **Reading may go on past the range.** The client reads only `length` bytes, so a provider can stop reading storage
  there or carry on to the end, whichever is simpler.

## Copying and moving

`AddResourceAsync` should store `NewResourceVersion.CopiedFrom` with the version and return it as
`ResourceInfo.CopiedFrom` on every read, as it does the content hash.

- **`CopyResourceAsync`'s default** reads the source and stores it through `AddResourceAsync`, so it copies correctly,
  just through the application. Override it to copy within your store, deciding the destination's condition as an
  upload does. `RestoreVersionAsync` calls it too, to copy an older version to its own name.
- **`MoveResourceAsync`'s default throws `NotSupportedException`.** A move must change every version's container and
  name at once, never leaving the resource in both places or half in each, so made of copies and deletes it could stop
  halfway. Implement it to keep each version's ID and attributes, refuse a destination name that has versions
  (`ResourceConflictException`) or whose container is missing (`ResourceContainerNotFoundException`), and decide the
  source conditions in the move itself.

## Importing versions

`ImportVersionAsync` is for [importing a container](../backup-and-move.md). It stores a version as it was in another
store: with the `VersionId` and `LastModified` it's given, rather than new ones, and with its type, metadata,
`CopiedFrom` and `ExpiresAt`. Otherwise it's an upload: measure and hash the content as `AddResourceAsync` does,
refuse a wrong `DeclaredLength`, and make the version the name's current one. Its default throws
`NotSupportedException`, so imports fail; exports work with any provider, because they only read.

`SetContainerMetadataAsync` replaces a container's metadata: an import uses it to mark the container it's
filling as unfinished, and to clear the mark at the end. Its default throws `NotSupportedException`, and imports then
go unmarked.

`GetVersionPageAsync` is for exports, which read a container through it 1,000 names at a time. It returns every version
of up to `maxNames` names after `afterName`: the names in `GetResourcePageAsync`'s order, and each name's versions in
`GetResourceVersionsAsync`'s, the current version first. Its default lists the names with `GetResourcePageAsync` and
then asks for each name's versions, a query apiece, so exports work with any provider, just more slowly than with one
that answers it in a single query. A provider that overrides neither `GetResourcePageAsync` nor this reads its whole
container once for every 1,000 names an export lists; override either one to avoid that.

## Expiry

`GetExpiredAsync`, `DeleteExpiredVersionAsync` and `SetExpiryAsync` default to throwing `NotSupportedException`, so
[expiry](../expiry.md) logs a warning and stops. `AddResourceAsync` and `CopyResourceAsync` should store
`NewResourceVersion.ExpiresAt` with the version and return it as `ResourceInfo.ExpiresAt` on every read.

A provider that doesn't support expiry doesn't refuse one, though: if `AddResourceAsync` stores the upload and drops
`ExpiresAt`, the clients can't tell, so the upload's result reports an expiry that will never happen. The same goes
for `CopiedFrom` on copies and restores, which is lost. Store both before anyone uses expiry with your provider.

To support it:

- **List expired versions from an index, a batch at a time.** Expiry calls `GetExpiredAsync` every interval on every
  instance, so it should cost little when nothing has expired. Order them by `ExpiresAt`, then ordinally by version
  ID, and start after the `after` version you're given: a sweep pages through every expired version this way, and
  fails if a provider lists the same batch twice.
- **Decide expiry in the delete.** `DeleteExpiredVersionAsync` deletes a version only if it's still expired when the
  delete runs, under whatever serializes writes to the name: `SetExpiryAsync` may have extended it since it was listed.
- **Change nothing but the expiry** in `SetExpiryAsync`.
- **Delete a batch together,** if you can. A sweep deletes each page of up to 100 expired versions it lists through
  `DeleteExpiredVersionsAsync`, which returns those it deleted. Decide expiry for each in the delete, as above, and
  make the next version current for a name whose current one goes. Its default calls `DeleteExpiredVersionAsync` for
  each version; and if a batch fails, the sweep tries each of its versions that way, so one version that keeps failing
  doesn't hold up the rest.
- **Delete what stopped uploads left**, if your provider stores content before the version is visible, as SQL
  Server's does. The sweep calls `DeleteAbandonedUploadsAsync(staleBefore)` first, every time, so make it cheap when
  there's nothing to delete. Delete only what an upload last added to before `staleBefore`, and return how many uploads'
  leftovers you deleted. Its default does nothing, for a provider that leaves nothing behind.

## Changing metadata and content type

`SetResourceMetadataAsync` and `SetContentTypeAsync` are for
[changing a version in place](../uploading.md#changing-metadata-and-content-type). Each changes one version, or the
current version when the version ID is `null`, and returns `false` when there's no such version. Change nothing else
about the version: its ID, content, hash and `LastModified` stay the same, and no new version is made. Store a copy of
the metadata you're given, so the caller can't change it afterward, and serialize the change with other writes to the
name, as for `SetExpiryAsync`. Their defaults throw `NotSupportedException`, so the clients' methods report that the
provider doesn't support them.

The service client's `SetContainerMetadataAsync` calls the provider member of the same name, described under
[Importing versions](#importing-versions), so with a provider that doesn't override it, the service client's method
throws `NotSupportedException`.

## Container usage

`GetContainerUsageAsync`'s default lists every resource's versions, one name at a time, and adds up their
`ContentLength`, so it reports usage correctly, just slowly for large containers.
[Container policies](../container-policies.md) call it before each upload they limit by size or count, so if your
store can add up a container's versions in one query, override it. Count every version in `StoredSize` and
`VersionCount`, and each name once in `ResourceCount`.

## A database to provision

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

If your provider keeps a database whose tables it creates and upgrades, derive a class from `ResourceDatabase`, in
`Resourcefull.Providers`, and register it for the store. `MigrateResourcefullDatabaseAsync` and
`GenerateResourcefullScript` then work for your provider as they do for SQL Server:

```csharp
builder.Services.AddKeyedSingleton<ResourceDatabase>(builder.StoreName, (services, _) => new FilesDatabase(root));
```

`MigrateAsync` brings the database up to date and does nothing when it already is. `GenerateScript` returns a script
that does the same, for a database administrator to run, and writes it without connecting to the database. A provider
with nothing to provision registers none; for its stores, `MigrateResourcefullDatabaseAsync` does nothing and
`GenerateResourcefullScript` throws `NotSupportedException`.

## Several stores

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

An application can add [several stores](../several-stores.md), each configured with its own options builder. A
provider that needs services of its own, such as its settings or a connection, registers them under the store's name,
`builder.StoreName`. It then makes the provider with the `UseProvider` overload that takes a factory, and resolves
those services by the same name:

```csharp
public static ResourcefullOptionsBuilder UseFiles(this ResourcefullOptionsBuilder builder, string root)
{
    string? storeName = builder.StoreName;
    builder.Services.AddKeyedSingleton(storeName, (_, _) => new FileStoreSettings(root));
    return builder.UseProvider(services =>
        new FileRepository(services.GetRequiredKeyedService<FileStoreSettings>(storeName)));
}
```

The store `AddResourcefull` adds has no name, and in .NET's own container a `null` key is the same as no key, so the
same code works for named and unnamed stores.

## Testing a provider

The repository's contract test suite, `src/Resourcefull.Testing`, is an abstract xUnit class that runs the
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
