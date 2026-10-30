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
- **Store `ExpiresAt` and `CopiedFrom`** <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> from the `ResourceInfo` that `AddResourceAsync` and `CopyResourceAsync`
  are given, and return them on every read. Nothing checks that a provider does: one that ignores them still accepts
  uploads with an expiry, and those versions never expire. See [Members added since 3.0](newer-members.md#expiry).
- **Return content as a read-only, forward-only stream:** `CanSeek` and `CanWrite` are `false`, `Length` is the
  content's full length, `Position` counts the bytes read, and seeking, setting `Position`, writing and `SetLength`
  throw `NotSupportedException`.
- **Store container metadata**, and return copies, so callers can't change what's stored. Storing `Uri` is optional:
  the clients replace it with the URI provider's whenever they return a container or version.
- **Honor cancellation:** a canceled operation throws `OperationCanceledException` and leaves nothing half-done. The
  one exception is deleting a container, which may stop between whole versions, as long as each remaining name keeps a
  current version and calling it again finishes the job.
- **Be safe to call concurrently** for the lifetime it registers with.

Members added to `IResourceRepository` after 3.0 have default implementations, so a provider written for an
earlier release keeps compiling. [Members added since 3.0](newer-members.md) says what each default does, and how to
answer the member directly.

## A skeleton to start from

Ten members have no default, so these are what a new provider writes first. Each comment says what the member must
do; the contract above and each member's XML documentation say the rest.

```csharp
public sealed class FileSystemRepository(FileSystemOptions options, TimeProvider? timeProvider = null)
    : IResourceRepository
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // Containers

    public Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo,
        CancellationToken cancellationToken = default)
        // Store the name and a copy of the metadata. A container that already exists throws
        // ResourceConflictException, decided by the write itself, not by a check before it.
        => throw new NotImplementedException();

    public Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default)
        // Every container, each with its own copy of the metadata.
        => throw new NotImplementedException();

    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        // The container, or null if there's none by that name (compared ordinally).
        => throw new NotImplementedException();

    public Task<bool> DeleteResourceContainerAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        // The container and every version in it; false if it didn't exist. It may stop between whole versions when
        // canceled, as long as every name left keeps its current version and calling it again finishes.
        => throw new NotImplementedException();

    // Writing

    public Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream,
        bool createOnly = false, CancellationToken cancellationToken = default)
        // Read the content through a HashingStream, store it as a new version, and make that version the name's
        // current one. With createOnly, a name that already has a version throws ResourceConflictException; a missing
        // container throws ResourceContainerNotFoundException. Return HashingStream.Complete(declared length, the
        // time from _clock as the version becomes current), and store its length and hash with the version.
        => throw new NotImplementedException();

    public Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null,
        CancellationToken cancellationToken = default)
        // One version, or every version of the name when versionId is null; false if there was nothing to delete.
        // Deleting the current version makes the next most recently stored one current.
        => throw new NotImplementedException();

    // Reading

    public Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        // The current version of every name in the container.
        => throw new NotImplementedException();

    public Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName,
        CancellationToken cancellationToken = default)
        // Every version of the name, the current one first, then from the most recently stored to the oldest.
        => throw new NotImplementedException();

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName,
        string? versionId = null, CancellationToken cancellationToken = default)
        // One version, or the current one when versionId is null; null if there's no such version. The version ID
        // comes from the caller unchecked: never let it, or the name, reach outside the name's own storage.
        => throw new NotImplementedException();

    public Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName,
        string? versionId = null, CancellationToken cancellationToken = default)
        // The version's content as a read-only, forward-only stream whose Length is the full length; null if there's
        // no such version.
        => throw new NotImplementedException();
}
```

The rest have defaults. Paging, reading a range, copying and container usage work through the members above, more
slowly than a provider that answers them itself. Conditional uploads and deletes, moving, importing, changing
container metadata and the three expiry members throw `NotSupportedException` until the provider implements them,
and the features that need them are refused. [Members added since 3.0](newer-members.md) covers each one.

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
