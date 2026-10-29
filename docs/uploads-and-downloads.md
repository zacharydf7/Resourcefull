# Uploads and downloads

## Uploading

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

### Content length and hashes

Every version records its length and its SHA-256 hash, both measured from the bytes as they're stored. `ContentHash`
has the form `sha256:` followed by 64 lowercase hex digits, so you can compare it with a hash you computed, use it to
find duplicates, or serve it as an HTTP ETag.

If you know the length in advance, declare it in `Headers.ContentLength`. An upload whose content turns out longer or
shorter throws `ResourceContentLengthMismatchException` and stores nothing, which catches a truncated request body.

Versions stored before hashes were recorded have `ContentHash = null` until the
[backfill](providers/sql-server.md#recording-hashes-for-older-versions) reaches them.

## Downloading

| Method | Returns | Use it for |
|---|---|---|
| `DownloadStreamingAsync` | a `ResourceDownloadResult`: the `ResourceInfo` and a `Stream` | anything large, or anything you pass on (to a response, a file) |
| `DownloadContentAsync` | a `ResourceContent`: the `ResourceInfo` and the bytes as `BinaryData` | small resources you need in memory |
| `GetResourceInfoAsync` | the `ResourceInfo` only | checking a version's length, type, hash or metadata without reading it |

All three return `null` when the resource (or the version you asked for) doesn't exist, and read the current version
unless you pass a `versionId`.

The stream from `DownloadStreamingAsync` reads from storage as you read it, so memory use stays flat whatever the
size. It's read-only and forward-only: `Length` is the content's length and `Position` counts the bytes read, but it
can't seek. Dispose it when you're done, which releases what the provider keeps for it (with SQL Server, a database
context).

```csharp
ResourceDownloadResult? download = await container.DownloadStreamingAsync("report.pdf");
if (download is not null)
{
    await using Stream content = download.Stream;
    await using FileStream file = File.Create("report.pdf");
    await content.CopyToAsync(file, cancellationToken);
}
```

The stream and the `ResourceInfo` describe the same version, even if another upload makes a newer version current
while you read. With SQL Server, if the version itself is deleted while you're reading it, the stream throws
`IOException` rather than ending early.

### Downloading part of a resource

Pass a `ResourceRange` to download only some of the bytes, such as to resume an interrupted download or to seek in a
video. It's an offset and a length, or an offset alone to read to the end:

```csharp
ResourceDownloadResult? part = await container.DownloadStreamingAsync("talk.mp4", versionId: null,
    new ResourceRange(Offset: 1_000_000, Length: 65_536), cancellationToken);
```

- **The stream holds just the range:** its `Length` is the range's length. `part.Range` says which bytes it holds, and
  `part.TotalLength` is the whole content's length, measured from what's stored.
- **A range that runs past the end is cut at the end,** and `part.Range` shows the cut. A range that starts past the
  end throws `ArgumentOutOfRangeException`.
- **Only the range is read from storage.** With SQL Server, reading the last MiB of a large file costs about what
  reading a 1 MiB file does.
- **To download a resource in parts,** pass the version ID from the first part's `ResourceInfo` to the rest, so a
  newer upload can't mix two versions in one file.

Every download's result has `TotalLength`; `Range` is `null` when it holds the whole content. To serve ranges over
HTTP, see [Range requests](recipes/aspnet-core.md#range-requests).

## Large files

Uploads and downloads both stream, so a file's size doesn't change how much memory they use. A few things still
matter with large files:

- **Pass streams, not byte arrays.** `BinaryData` and `DownloadContentAsync` hold the whole file in memory.
- **A failed upload can be retried only if its stream can seek.** With SQL Server, an upload is one transaction. If a
  transient failure interrupts it, Resourcefull rewinds a seekable stream (a `FileStream`, a `MemoryStream`, the
  `BinaryData` overload) and tries again. A stream that can't seek, such as an HTTP request body, fails instead; the
  caller can upload again. Nothing is stored either way.
- **Web servers limit request sizes.** See [large uploads in ASP.NET Core](recipes/aspnet-core.md#large-uploads).

## Cancellation

Every method that contacts storage has an overload that takes a `CancellationToken` last. A cancelled operation
throws `OperationCanceledException`; it never returns `null`, an empty list or a result for work it didn't finish.

```csharp
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
ResourceInfo stored = await container.UploadAsync("backup.zip", file, null, timeout.Token);
```

- **A cancelled upload stores nothing.** Once the new version is stored, though, the upload has succeeded, and the
  token no longer applies. Pruning older versions then runs to completion (see [Versioning](versioning.md)).
- **`DownloadStreamingAsync`'s token covers finding the resource.** To cancel reading the content, pass a token to
  the stream's `ReadAsync` or `CopyToAsync`. `DownloadContentAsync` does that for you.
- **Deleting a container can stop partway**, between whole versions; see
  [Containers and names](containers-and-names.md#containers).
