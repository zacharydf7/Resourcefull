# Writing a provider

To store resources somewhere else, such as a file system, object storage or another database, derive a class from
`ResourceRepository` (in the `Resourcefull.Providers` namespace) and register it with `UseProvider`. The clients handle
names, versioning, pruning, URIs and cancellation plumbing; a provider stores and retrieves.

<span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> This page describes 4.0, where providers derive
from the `ResourceRepository` base class. In 3.1, a provider implements the `IResourceRepository` interface instead; see
[Upgrading to 4.0](../upgrading-to-4.md#custom-providers) for moving a provider across.

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

Put your `Use…` extension in the `Resourcefull` namespace, as the built-in providers do, so an application switches to
your provider by changing one line and no `using`.

Every built-in provider aims to support every feature, and a new one ships with an issue for each feature it doesn't
support yet; [Provider features](features.md) shows where each stands. A provider of your own can leave features out:
the clients throw `NotSupportedException` for those, and the contract tests skip them when its test class says so.

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
  changes nothing. `conditions.ThrowIfNotMet(resource, currentVersionId, currentContentHash)`
  <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> does exactly this check, so call it with the
  current version the write reads; `IsEmpty` tells you when there's nothing to check. It's an extension method in
  `Resourcefull.Providers`, as are the other helpers for providers.
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
  `CopyResourceAsync` are given, and return them on every read. A version only has an `ExpiresAt` if your provider's
  `SupportsExpiry` is `true`; see [Expiry](#expiry).
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

    public override Task AddContainerAsync(string containerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        // Store the name and a copy of the metadata. A container that already exists throws
        // ResourceConflictException, decided by the write itself, not by a check before it.
        => throw new NotImplementedException();

    public override Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName,
        int pageSize, CancellationToken cancellationToken = default)
        // Up to pageSize containers whose names start with prefix and come after afterName, ordered by name, each
        // with its own copy of the metadata. Seek on the name rather than reading every container.
        => throw new NotImplementedException();

    public override Task<ContainerInfo?> GetContainerInfoAsync(string containerName,
        CancellationToken cancellationToken = default)
        // The container, or null if there's none by that name (compared ordinally).
        => throw new NotImplementedException();

    public override Task<bool> DeleteContainerAsync(string containerName,
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

    public override Task<bool> DeleteResourceAsync(ResourceAddress resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default)
        // One version, or every version of the name when versionId is null; false if there was nothing to delete.
        // Deleting the current version makes the next most recently stored one current. Conditions the current
        // version doesn't meet throw ResourcePreconditionFailedException.
        => throw new NotImplementedException();

    // Reading

    public override Task<IReadOnlyList<ResourceInfo>> GetResourcePageAsync(string containerName, string? prefix,
        string? afterName, int pageSize, CancellationToken cancellationToken = default)
        // Up to pageSize current versions whose names start with prefix and come after afterName, ordered by name:
        // see Listing by page. Answer it without reading the whole container.
        => throw new NotImplementedException();

    public override Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceAddress resource,
        CancellationToken cancellationToken = default)
        // Every version of the name, the current one first, then from the most recently stored to the oldest.
        => throw new NotImplementedException();

    public override Task<ResourceInfo?> GetResourceInfoAsync(ResourceAddress resource, string? versionId = null,
        CancellationToken cancellationToken = default)
        // One version, or the current one when versionId is null; null if there's no such version. The version ID
        // comes from the caller unchecked: never let it, or the name, reach outside the name's own storage.
        => throw new NotImplementedException();

    public override Task<Stream?> GetResourceStreamAsync(ResourceAddress resource, string? versionId = null,
        CancellationToken cancellationToken = default)
        // The version's content as a read-only, forward-only stream whose Length is the full length; null if there's
        // no such version.
        => throw new NotImplementedException();
}
```

The rest are virtual, with defaults. Reading a range, copying and container usage work through the members above,
more slowly than a provider that answers them itself. Moving, importing, changing a container's metadata,
changing a version's metadata or content type in place, and `GetExpiredAsync`, `DeleteExpiredVersionAsync` and
`SetExpiryAsync` throw `NotSupportedException` until the provider overrides them, and the features that need them
report that they aren't supported. The sections below say what each default does, and what to do to
override it.

## Listing by page

`GetResourcePageAsync` is how every listing reads a container, so it has no default: return at most `pageSize` current
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
that answers it in a single query.

## Expiry

`SupportsExpiry` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> is `false` by default, for a provider that leaves
[expiry](../expiry.md) out. While it's `false`, every write that sets an expiry throws `NotSupportedException`, so
nothing is stored with an expiry that never happens, and `UseExpiry` fails at startup with
`InvalidOperationException`. `GetExpiredAsync`, `DeleteExpiredVersionAsync` and `SetExpiryAsync` default to throwing
`NotSupportedException`.

To support expiry, override `SupportsExpiry` to return `true`, store `NewResourceVersion.ExpiresAt` with the version in
`AddResourceAsync` and `CopyResourceAsync`, and return it as `ResourceInfo.ExpiresAt` on every read. Then:

- **List expired versions from an index, a batch at a time.** Expiry calls `GetExpiredAsync` every interval on every
  instance, so it should cost little when nothing has expired. Order them by `ExpiresAt`, then ordinally by version
  ID, then by container and name, and start after the `after` key you're given: a sweep pages through every expired
  version this way, and fails if a provider lists the same batch twice. `ResourceVersionKey` holds all four because
  version IDs are unique only within a name: a copied container's versions keep their IDs and expiries.
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
  leftovers you deleted. Its default does nothing, for a provider that leaves nothing behind. Only the sweep calls it,
  so a provider that leaves anything behind should support expiry, or its leftovers stay.

## Upload sessions

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

[Upload sessions](../uploading-in-parts.md) store a version's content over several appends, which may come from
different processes, and make it current when the session completes. A provider supports them by overriding six
members, which work on a staged version: one that only these members can see, just as an upload's version stays hidden
while its content arrives.

- **`StartStagedVersionAsync`** stages a version with no content, keeping its content type, metadata, expiry and
  declared length. A missing container throws `ResourceContainerNotFoundException`.
- **`GetStagedVersionAsync`** returns the version as it was staged and how many bytes are stored, or `null`.
- **`AppendStagedContentAsync`** appends content, provided the stored content ends at the offset it's given; otherwise
  it throws `ResourceUploadOffsetException` with the stored length, and stores nothing. A provider that can hold only so
  much in one session throws `ResourceUploadLimitException` once the session is full. It may store the content as it
  arrives, so an append that fails partway keeps what it stored. Make the check and the store one step, as SQL Server's
  provider does by moving the stored length forward only from the length it expects, in the transaction that stores each
  batch, so two appends from the same offset can't both succeed. When your provider retries a batch after a transient
  failure, and the earlier attempt's commit succeeded but its acknowledgment was lost, the retry must recognize its own
  stored batch rather than refuse it.
- **`GetStagedStreamAsync`** reads the staged content from the start. The clients read it once, to measure, hash and
  inspect it, before they complete the session.
- **`CompleteStagedVersionAsync`** makes the version current with the length and hash it's given, deciding the write
  condition as `AddResourceAsync` does. Refuse a length that isn't what's stored with `ResourceUploadOffsetException`:
  an append landed after the content was measured. Lock against appends while you check, so none lands between the
  check and the change.
- **`DeleteStagedVersionAsync`** deletes a staged version and its content.

Each append should move the version's stamp forward, so `DeleteAbandonedUploadsAsync` deletes a session only once
nothing has been appended to it for a day. Only the [expiry](#expiry) sweep calls `DeleteAbandonedUploadsAsync`, so with
a provider that doesn't support expiry, an abandoned session stays until the application cancels it. For a session that
doesn't exist, `DeleteStagedVersionAsync` returns `false`, and the others, except `StartStagedVersionAsync`, return
`null`. The defaults throw `NotSupportedException`, so with a provider that doesn't override them, the clients' session methods throw it too.

## Changing metadata and content type

`SetResourceMetadataAsync` and `SetContentTypeAsync` are for
[changing a version in place](../uploading.md#changing-metadata-and-content-type). Each changes one version, or the
current version when the version ID is `null`, and returns `false` when there's no such version. Change nothing else
about the version: its ID, content, hash and `LastModified` stay the same, and no new version is made. Store a copy of
the metadata you're given, so the caller can't change it afterward, and serialize the change with other writes to the
name, as for `SetExpiryAsync`. Their defaults throw `NotSupportedException`, so the clients' methods report that the
provider doesn't support them.

The container client's `SetMetadataAsync` calls the provider's `SetContainerMetadataAsync`, described under
[Importing versions](#importing-versions), so with a provider that doesn't override it, `SetMetadataAsync` throws
`NotSupportedException`.

## Retention and legal holds

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

[Retention dates and legal holds](../deleting-safely.md) keep versions from being deleted. `SupportsRetention` is
`false` by default, and the clients then refuse every write that would protect a version, with
`NotSupportedException`, so nothing is stored unprotected. To support them, return `true` and:

- **Store and return both.** Keep `NewResourceVersion.RetainUntil` and `LegalHold` with each version that
  `AddResourceAsync`, `ImportVersionAsync`, `CopyResourceAsync` and the [upload session](#upload-sessions) members
  store, and return them as `ResourceInfo.RetainUntil` and `LegalHold` on every read. A move keeps both with each
  version.
- **Override `SetRetentionAsync` and `SetLegalHoldAsync`.** Each changes one version, or the current one for a `null`
  version ID, and returns `false` when there's no such version. Refuse a date before the version's own with
  `ArgumentOutOfRangeException`, deciding that in the change, under whatever serializes writes to the name.
- **Refuse protected deletes in the delete itself.** `DeleteResourceAsync` throws `ResourceRetainedException` when
  any version it would delete can't be deleted now (`ResourceInfo.IsDeletableAt` is `false`), and deletes nothing;
  `ResourceRetainedException.Create`, from `Resourcefull.Providers`, builds it with the clients' message. Decide it as
  you decide conditions, in the same step as the delete, so a hold set at the same moment either lands first and stops
  the delete or finds the version gone. `DeleteContainerAsync` does the same for any version in the container. A
  container deleted in batches must also refuse a version protected after the delete started, before it deletes that
  version.
- **Leave protected versions to expiry.** `GetExpiredAsync` lists only versions that may be deleted, and
  `DeleteExpiredVersionAsync` and `DeleteExpiredVersionsAsync` leave a protected version as they leave one whose
  expiry was extended.

## The bin

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

The [bin](../deleting-safely.md#the-bin) keeps deleted versions for a while. `SupportsSoftDelete` is `false` by
default, and the clients then refuse every delete in a container whose policy keeps deleted versions, with
`NotSupportedException`, so nothing meant to be kept is deleted for good. To support it, support
[expiry](#expiry), whose sweep purges the bin, return `true`, and:

- **Override `SoftDeleteResourceAsync`.** It deletes as `DeleteResourceAsync` does, deciding conditions and retention
  the same way, but keeps the versions, each with `DeletedAt` set to now and `PurgeAt` to the time it's given, and
  leaves their content in place.
- **Leave the bin out of everything else.** A version in the bin isn't returned by any read, listing, version list,
  search or `GetExpiredAsync`, isn't counted in `GetContainerUsageAsync` but for `DeletedSize` and
  `DeletedVersionCount`, and isn't moved by `MoveResourceAsync`. A name whose versions are all in the bin is free for
  a create-only write, but a version ID in the bin stays taken. `DeleteContainerAsync` deletes the bin too.
- **List it.** `GetDeletedPageAsync` lists a container's bin newest deletion first, then by version ID and name, and
  returns where the next page starts; with a prefix, it may stop early and return a short page rather than read the whole
  bin. `GetDeletedVersionsAsync` lists one name's.
- **Bring versions back.** `UndeleteAsync` puts them back among the name's older versions in the order they were first
  stored, so the current version stays current; if the name has none, the most recently stored becomes current.
- **Purge it.** `PurgeDeletedAsync` deletes versions in the bin for good, and with a time, only those due by then,
  deciding that in the purge itself. `GetPurgeableAsync` lists what's due across every container for the sweep, so
  answer it from an index, as `GetExpiredAsync` is, and order it the same way, with `PurgeAt` in place of `ExpiresAt`.

## Metadata search

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

[Searching by metadata](../listing.md#finding-resources-by-metadata) works with every provider:
`FindResourcePageAsync`'s default reads `GetResourcePageAsync`'s pages and keeps the matches. That reads every name, so
a provider whose store can keep an index should override it:

- **Index the searchable keys.** `SearchableMetadataKeys` holds the keys the application named with
  `UseMetadataSearch`. Your provider gets them through the base class's constructor: read them from the store's
  `ResourcefullOptions` in the factory that makes the provider, as below, where `storeName` is `builder.StoreName`
  (see [Several stores](#several-stores)). Keep an index of each current version's
  values for those keys, and update it in the same step as any write that changes which version is current or changes
  the current version's metadata: uploads, copies, restores, imports, completed upload sessions, moves, deletes, expiry
  and `SetResourceMetadataAsync`. Keys are at most 128 characters. The clients refuse a value over 256 characters for
  these keys, in writes and searches alike, but a version stored before its key was named can still have one: leave it
  out of the index.
- **Override `FindResourcePageAsync`** to list the current versions with the given key and value, as
  `GetResourcePageAsync` lists them: in name order, with the prefix, after the name given, up to the page size.
- **Override `IndexMetadataAsync`** to index a container's current versions, for keys added after they were stored. Its
  default does nothing, which suits a provider without an index.

```csharp
public sealed class FileRepository(FileStoreSettings settings, IReadOnlySet<string> searchableMetadataKeys)
    : ResourceRepository(searchableMetadataKeys)
{
    // ...
}

builder.UseProvider(services => new FileRepository(services.GetRequiredKeyedService<FileStoreSettings>(storeName),
    services.GetRequiredService<IOptionsMonitor<ResourcefullOptions>>().Get(storeName ?? Options.DefaultName)
        .SearchableMetadataKeys));
```

A provider that wraps another, such as one that caches or encrypts, passes `FindResourcePageAsync` and
`IndexMetadataAsync` on, so the wrapped provider's index answers searches.

## Container usage

`GetContainerUsageAsync`'s default lists every resource's versions, one name at a time, and adds up their
`ContentLength`, so it reports usage correctly, just slowly for large containers.
[Container policies](../container-policies.md) call it before each upload they limit by size or count, so if your
store can add up a container's versions in one query, override it. Count every version in `StoredSize` and
`VersionCount`, and each name once in `ResourceCount`, leaving out the [bin](#the-bin), which goes in `DeletedSize` and
`DeletedVersionCount`.

`GetTopVersionsAsync` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> lists a container's
largest versions, or those that expire soonest, for the [dashboard](../aspnet-core/dashboard.md) and an
administrator's own tools. Its default reads every version through `GetVersionPageAsync` and keeps the top ones, so it
works with any provider. If your store can sort a container's versions by length or expiry, override it with one
query. For `LargestFirst`, order by `ContentLength` descending, with versions that have none last. For
`ExpiringFirst`, leave out versions without an `ExpiresAt` and order by it ascending. In both, break ties by version
ID, ordinally.

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
same code works for named and unnamed stores. A provider that indexes metadata for search also passes the store's
search keys, as in [Metadata search](#metadata-search).

## Testing a provider

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

The `Resourcefull.Testing` package holds the contract tests: about 200 tests of what every provider must do, from
round-tripping content and keeping versions to conditional writes, listings, policies and names. Every built-in
provider passes them, and they're versioned with the core, so a new release's tests check what that release expects.

They're written for xUnit v3, and need `xunit.v3` 4.0.2 or later. Add the package to an xUnit v3 test project:

```shell
dotnet add package Resourcefull.Testing
```

Then derive a class from `ResourcefullContractTests` that registers your provider. The suite calls
`BuildServiceProvider` once per test, and each test works in containers of its own, so all the tests can share one
store:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Testing;

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

    protected override bool SharesStoreBetweenServiceProviders => false;
}
```

The tests assume that the service providers `BuildServiceProvider` builds share one store, as two instances of an
application would. This example gives each one a new folder, so it overrides `SharesStoreBetweenServiceProviders` to
return `false`, and the few tests that store with one service provider and read with another are skipped.

Run `dotnet test`, and xUnit runs every test the class inherits. A store that needs a server, such as a database,
can skip its tests when none is available: call `Assert.SkipUnless` in `BuildServiceProvider`.

### Features your provider leaves out

When your provider leaves out an optional feature, the suite skips that feature's tests. In their place, a test checks
that the clients refuse the feature with `NotSupportedException`, changing nothing, rather than seem to succeed.

For expiry, [retention](#retention-and-legal-holds) and [the bin](#the-bin), the suite reads your provider's own
`SupportsExpiry`, `SupportsRetention` and `SupportsSoftDelete`, so there's nothing to set in your test class. It gets
them from the unnamed store's `ResourceRepository`, so register your provider there, as above. A test also checks that
a provider with a bin supports expiry.

The other features have no such flag, so turn off a feature's switch in your test class when your provider leaves it
out:

| Switch | Turn it off when the provider doesn't override |
|---|---|
| `SupportsContainerMetadata` | `SetContainerMetadataAsync` |
| `SupportsVersionChanges` | `SetResourceMetadataAsync` and `SetContentTypeAsync` |
| `SupportsMove` | `MoveResourceAsync` |
| `SupportsImport` | `ImportVersionAsync` |
| `SupportsUploadSessions` | the six [upload session](#upload-sessions) members |

```csharp
public class FileSystemContractTests : ResourcefullContractTests
{
    protected override bool SupportsMove => false;

    // BuildServiceProvider as above.
}
```

A switch added after 4.0 is off by default, so that a new release's tests don't fail a provider written before the
feature existed. Turn it on once your provider supports the feature.

The members with a default that works, such as `CopyResourceAsync` and `GetContainerUsageAsync`, need no switch: the
suite tests them either way.

### How the store keeps things

Two more settings describe a store that keeps times or orders names differently from .NET:

- **`TimePrecision`**: how precisely the store keeps times. A time a test gives, such as an expiry, is expected back
  cut to this precision. It's exact by default; PostgreSQL's contract tests set a microsecond.
- **`NameOrder`**: the order the store lists names in. It's `StringComparer.Ordinal` by default; PostgreSQL's
  contract tests set Unicode code point order, which is PostgreSQL's byte order.
