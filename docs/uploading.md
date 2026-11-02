# Uploading files

Uploading stores a file under a name in a container, with its content type and any metadata you give it, and
returns a description of what was stored. This page covers the options an upload takes, what it returns, and how
Resourcefull checks each file's length and hash as it's stored.

`UploadAsync` takes the content as a `Stream` or as `BinaryData`:

```csharp
ResourceInfo stored = await container.UploadAsync("report.pdf", stream, new ResourceUploadOptions
{
    Overwrite = true,
    Headers = new ResourceHeaders("application/pdf", ContentLength: null),
    Metadata = new Dictionary<string, string> { ["author"] = "finance" }
});
```

- **The stream is read from its current position to its end**, like `Stream.CopyTo`. Rewind a stream you've just
  written (`stream.Position = 0`) before uploading it, or the upload stores an empty version, which with `Overwrite`
  replaces the existing content. Resourcefull doesn't dispose the stream.
- **What's returned is what was stored:** the new version's `VersionId`, the `ContentLength` measured from the bytes
  read, the `ContentHash`, `LastModified` (when the version became current) and `IsLatest = true`.
- **Overwriting.** Without versioning, a name holds one version: an upload to an existing name throws
  `ResourceConflictException` unless `Overwrite` is set. With versioning, every upload adds a version, and
  `Overwrite` is ignored. Set `CreateOnly` to store the upload only if the name doesn't exist yet, with or without
  versioning.
- **Content type and metadata** are stored as given and returned on every read. Resourcefull doesn't check the type
  against the content, but an [upload inspector](upload-inspection.md) <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span> can, and can scan the file for
  malware before it's stored.

## Changing metadata and content type

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

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
