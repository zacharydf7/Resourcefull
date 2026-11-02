# Upgrading to 4.0

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

4.0 is a major release: besides its new features, it changes the API in ways that need changes to your code. This page
lists each change and how to move your code across. 4.0 isn't released yet, so the list grows as its changes are made.
For upgrades within a major version, and for rolling an upgrade out, see [Upgrading](upgrading.md).

## Upgrading your code

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
