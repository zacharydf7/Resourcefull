# Uploading files

Uploading stores a file under a name in a container, with its content type and any metadata you give it, and
returns a description of what was stored. This page covers the options an upload takes, what it returns, and how
Resourcefull checks each file's length and hash as it's stored.

`UploadAsync` takes the content as a `Stream` or as `BinaryData`:

```csharp
ResourceInfo stored = await container.UploadAsync("report.pdf", stream, new ResourceUploadOptions
{
    Mode = ResourceWriteMode.Replace,
    Headers = new ResourceHeaders("application/pdf", ContentLength: null),
    Metadata = new Dictionary<string, string> { ["author"] = "finance" }
});
```

- **The stream is read from its current position to its end**, like `Stream.CopyTo`. Rewind a stream you've just
  written (`stream.Position = 0`) before uploading it, or the upload stores an empty version, which with `Replace`
  replaces the existing content. Resourcefull doesn't dispose the stream.
- **What's returned is what was stored:** the new version's `VersionId`, the `ContentLength` measured from the bytes
  read, the `ContentHash`, `LastModified` (when the version became current) and `IsLatest = true`.
- **A name that exists is refused** unless the upload asks to replace it; see [Replacing a file](#replacing-a-file).
- **Content type and metadata** are stored as given and returned on every read. Resourcefull doesn't check the type
  against the content, but an [upload inspector](upload-inspection.md) <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> can, and can scan the file for
  malware before it's stored.

## Replacing a file

`ResourceUploadOptions.Mode` <span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> says what an upload does when its name already has a current version:

- **`ResourceWriteMode.Create`, the default,** stores the upload only if the name doesn't exist yet, and otherwise
  throws `ResourceConflictException` and stores nothing. The check is part of the write, so of several uploads of a
  new name at once, exactly one succeeds.
- **`ResourceWriteMode.Replace`** stores the upload as the name's new current version, whether or not the name
  exists. What happens to the version it replaced depends on the container's [version limit](versioning.md): with
  one, the default, it's deleted, and with more it's kept as an older version.

[Conditions](concurrency.md#updating-only-if-unchanged) replace a version only if it's still the one you read, so
they go with `Replace`; with `Create`, an upload that sets them throws `ArgumentException`.

<span class="rf-badge rf-upcoming" title="Coming in 4.0">4.0</span> In 3.1, `Overwrite` and `CreateOnly` do this instead: without versioning, an upload to an existing name
is refused unless `Overwrite` is set; with versioning, every upload adds a version unless `CreateOnly` is set.

## Changing metadata and content type

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

A version's metadata and content type can change after it's stored, without uploading it again:

```csharp
await container.SetResourceMetadataAsync("report.pdf", versionId: null,
    new Dictionary<string, string> { ["author"] = "finance", ["reviewed"] = "yes" });

await container.SetContentTypeAsync("report.pdf", versionId: null, "application/pdf");
```

- **The version changes in place.** No new version is made, even with [versioning](versioning.md), and the version
  keeps its ID, content, `LastModified` and ETag. Pass a version ID to change an older version, or `null` for the
  current one. Each returns `false` when there's no such version.
- **Metadata is replaced whole.** To add or change one entry, read the version's metadata, change it and set it. Two
  callers doing that to the same version at once can each overwrite the other's change, and conditions can't stop
  that, since the version's ID and content hash stay the same.
- **Copies already downloaded keep the old type.** A browser or cache that holds the version asks whether its copy is
  still current, and since the ETag and `LastModified` haven't changed, it's told yes. A wrong type is best fixed by
  uploading again when files are served to browsers that cache them.
- **A new content type is checked** against the container's [`AllowedContentTypes`](container-policies.md), as an
  upload's is. An [upload inspector](upload-inspection.md) doesn't see the change, so if yours checks that a file is
  the type it claims, check the new type yourself before setting it.
- **A [read-only](container-policies.md) container refuses both.**

## Content length and hashes

Every version records its length and its SHA-256 hash, both measured from the bytes as they're stored. `ContentHash`
has the form `sha256:` followed by 64 lowercase hex digits, so you can compare it with a hash you computed, use it to
find duplicates, or serve it as an HTTP ETag.

If you know the length in advance, declare it in `Headers.ContentLength`. An upload whose content turns out longer or
shorter throws `ResourceContentLengthMismatchException` and stores nothing, which catches a truncated request body.

Versions stored before hashes were recorded have `ContentHash = null` until the
[backfill](providers/sql-server-upgrades.md#recording-hashes-for-older-versions) reaches them.
