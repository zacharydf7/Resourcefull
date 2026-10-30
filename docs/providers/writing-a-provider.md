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
- **Return content as a read-only, forward-only stream:** `CanSeek` and `CanWrite` are `false`, `Length` is the
  content's full length, `Position` counts the bytes read, and seeking, setting `Position`, writing and `SetLength`
  throw `NotSupportedException`.
- **Store container metadata**, and return copies, so callers can't change what's stored. Storing `Uri` is optional:
  the clients replace it with the URI provider's whenever they return a container or version.
- **Honor cancellation:** a cancelled operation throws `OperationCanceledException` and leaves nothing half-done. The
  one exception is deleting a container, which may stop between whole versions, as long as each remaining name keeps a
  current version and calling it again finishes the job.
- **Be safe to call concurrently** for the lifetime it registers with.

Members added to `IResourceRepository` after 3.0 have default implementations, so a provider written for an
earlier release keeps compiling. [Members added since 3.0](newer-members.md) says what each default does, and how to
answer the member directly.

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
