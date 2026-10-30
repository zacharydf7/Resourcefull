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
- **Content type and metadata** are stored as given and returned on every read. Resourcefull doesn't inspect the
  content.

## Content length and hashes

Every version records its length and its SHA-256 hash, both measured from the bytes as they're stored. `ContentHash`
has the form `sha256:` followed by 64 lowercase hex digits, so you can compare it with a hash you computed, use it to
find duplicates, or serve it as an HTTP ETag.

If you know the length in advance, declare it in `Headers.ContentLength`. An upload whose content turns out longer or
shorter throws `ResourceContentLengthMismatchException` and stores nothing, which catches a truncated request body.

Versions stored before hashes were recorded have `ContentHash = null` until the
[backfill](providers/sql-server-upgrades.md#recording-hashes-for-older-versions) reaches them.
