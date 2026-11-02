# Upgrading to 4.0

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

4.0 is a major release: besides its new features, it changes the API in ways that need changes to your code. This page
lists each change and how to move your code across. 4.0 isn't released yet, so the list grows as its changes are made.
For upgrades within a major version, and for rolling an upgrade out, see [Upgrading](upgrading.md).

## Upgrading your code

### Uploads say whether they replace

`ResourceUploadOptions.Overwrite` and `CreateOnly`, and the same options on `ResourceCopyOptions`, are replaced by
`Mode`, a `ResourceWriteMode`. What an upload does no longer depends on whether versioning is on:

- **`Create`, the default,** stores the upload only if the name doesn't exist yet.
- **`Replace`** stores it as the name's new current version. The container's version limit decides whether the version
  it replaced is kept.

```csharp
// Before
await container.UploadAsync("report.pdf", file, new ResourceUploadOptions { Overwrite = true });

// 4.0
await container.UploadAsync("report.pdf", file, new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
```

Check these, even where your code compiles:

- **With versioning on, an upload of a name that exists is refused** unless it sets `Mode = ResourceWriteMode.Replace`.
  In 3.x, it added a version. Without versioning, `Overwrite = true` becomes `Replace`, and `CreateOnly = true` is now
  the default, so you can leave it out.
- **Conditions need `Replace`.** An upload or copy with `Conditions` or `DestinationConditions` and the default `Create`
  throws `ArgumentException`.
- **`ResourcefullOptions.IsVersioningEnabled` is gone.** `MaxVersions` is 1 without versioning, and a
  [container policy](container-policies.md) can set its own `MaxVersions` for a container.
- **`RestoreVersionAsync` works with versioning off,** where the version still exists, rather than throwing
  `InvalidOperationException`.
- **`UploadFromRequestAsync` replaces, as a `PUT` does,** and ignores `Mode`, so setting `Mode = ResourceWriteMode.Create`
  there doesn't make it create-only. Where your 3.x code set `CreateOnly` for it, have the client send
  `If-None-Match: *`, or have the endpoint require that header.

### One method per operation

Each operation of `IResourceContainerClient` and `IResourceServiceClient` is now one method: its required parameters,
then its optional ones, then an optional `CancellationToken` last. In 3.x, the methods from before 3.1 came in pairs,
and only the one that required every parameter took a token. Most calls compile unchanged, and a token can now be
named instead of passing `null` for what comes before it. A call like this one, which passes a token after a version
ID, has to name it:

```csharp
// Before
ResourceDownloadResult? download = await container.DownloadStreamingAsync(name, null, cancellationToken);

// 4.0
ResourceDownloadResult? download = await container.DownloadStreamingAsync(name, cancellationToken: cancellationToken);
```

These need changes:

- **A token passed after a version ID** to `DownloadStreamingAsync` or `DeleteResourceIfExistsAsync`, as in the
  example above. The parameter after the version ID is now the range or the conditions, so name the token.
- **`GetResourcesAsync`** is gone, because it read a whole container into memory at once. Enumerate
  `ListResourcesAsync`, or for a small container, use `await container.ListResourcesAsync().ToListAsync()`. See
  [Listing resources](listing.md).
- **`UploadAsync`'s `uploadOptions` parameter** is named `options`, as on the other methods, so a call that names it
  changes.
- **Mocks and test doubles.** A Moq setup or verification can't leave out optional arguments, so it passes the token
  too: `Setup(c => c.ExistsAsync("a", null, It.IsAny<CancellationToken>()))`. A class that implements either
  interface implements one method per operation, and handles the range and the conditions itself: the overloads
  that took them no longer have default implementations.

### `ResourceInfo` can't be changed

`ResourceInfo` is now a record whose properties are set only when it's created, and its `Metadata` is an
`IReadOnlyDictionary<string, string>`. It describes a version as it was stored, so changing it never changed what's
stored anyway.

- **To change a stored version's metadata or content type,** call `SetResourceMetadataAsync` or `SetContentTypeAsync`
  on the container client. See [Changing metadata and content type](uploading.md#changing-metadata-and-content-type).
- **To make a changed copy** for your own use, such as a view model, use a `with` expression:
  `info with { ContentType = "text/plain" }`.
- **It's sealed,** so a class can't derive from it. Wrap it, or copy what you need, instead.
- **`VersionId` is `required`,** so code that creates a `ResourceInfo`, such as a test fake, must set it.

### Container clients aren't disposable

`IResourceContainerClient` no longer implements `IDisposable`. Its `Dispose` did nothing, so remove `using` where you
call `GetResourceContainerClient`:

```csharp
// Before
using IResourceContainerClient container = service.GetResourceContainerClient("reports");

// 4.0
IResourceContainerClient container = service.GetResourceContainerClient("reports");
```

### Custom providers

A provider now derives from the `ResourceRepository` base class rather than implementing the `IResourceRepository`
interface. Code that resolves the provider from dependency injection, or mocks it in tests, uses `ResourceRepository`
too; with Moq, `new Mock<ResourceRepository> { CallBase = true }` keeps the members' default implementations.

To move a provider across, change `: IResourceRepository` to `: ResourceRepository`, mark each member `override`, and
change the members whose signatures changed:

| 3.x | 4.0 |
|---|---|
| The two `AddResourceAsync` overloads, one taking `bool createOnly` and one taking a `ResourceWriteCondition` | One `AddResourceAsync(NewResourceVersion version, Stream content, ResourceWriteCondition? condition, ...)`; `condition` is `null` for no condition, and `createOnly` is now `condition.CreateOnly` |
| The two `DeleteResourceAsync` overloads, with and without `ResourceConditions` | One, with `ResourceConditions? conditions`, `null` for no condition |
| Reads and deletes taking `string resourceContainerName, string resourceName` | A `ResourceLocation` |
| `AddResourceContainerAsync(ResourceContainerInfo)` | `AddResourceContainerAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata, ...)` |
| `CopyResourceAsync`'s `ResourceInfo destination` | A `NewResourceVersion` |
| `ImportVersionAsync(string resourceContainerName, ResourceInfo version, ...)` | `ImportVersionAsync(NewResourceVersion version, ...)` |
| `GetExpiredAsync`'s `ResourceInfo? after` | `(DateTimeOffset ExpiresAt, string VersionId)? after`: the expiry and version ID of the previous batch's last version |
| `IDictionary<string, string>` metadata parameters | `IReadOnlyDictionary<string, string>` |
| Lists returned as `List<T>` | `IReadOnlyList<T>` |

`NewResourceVersion` holds what a write is given: the location, version ID, content type, declared length
(`DeclaredLength`, which was `ContentLength`), metadata, expiry and origin, and for an import, `LastModified`. It has
none of the properties a write's caller couldn't know, such as `IsLatest`, `ContentHash` and `Uri`. Build a
`ResourceInfo` for each read as before, but leave `Uri` unset: the clients fill it in from your URI provider, and
providers no longer store it.

Conditional uploads and deletes are now part of the abstract members, rather than overloads whose defaults throw
`NotSupportedException`, so every provider decides conditions itself. See [The contract](providers/writing-a-provider.md#the-contract).

A provider package for 4.0 can depend on `[4.0.0, 5.0.0)`. See [Writing a provider](providers/writing-a-provider.md).
