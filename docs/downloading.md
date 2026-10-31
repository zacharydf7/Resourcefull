# Downloading files

Resourcefull gives you a file's content as a stream to read as you go, or as bytes in memory, or just its
description without the content. You can also download part of a file, which is how video players seek and download
managers resume.

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
context). From 3.2 <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, disposing the download disposes its stream, so
`await using ResourceDownloadResult? download = await container.DownloadStreamingAsync(...)` does it too.

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
`IOException` rather than ending early. That includes an upload that replaces it: without versioning, each upload
deletes the version before it, so a download of a file that's replaced often can fail partway and needs a retry. With
[expiry](expiry.md) turned on <span class="rf-badge rf-upcoming" title="Coming in 3.2">3.2</span>, a replaced version stays five minutes more, so a download that takes less
than that finishes.

## Downloading part of a resource

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 3.2</span></div>

Pass a `ResourceRange` to download only some of the bytes, such as to resume an interrupted download or to seek in a
video. It's an offset and a length, or an offset alone to read to the end:

```csharp
ResourceDownloadResult? part = await container.DownloadStreamingAsync("talk.mp4", versionId: null,
    new ResourceRange(Offset: 1_000_000, Length: 65_536), cancellationToken);
```

- **The stream holds just the range:** its `Length` is the range's length. `part.Range` says which bytes it holds, and
  `part.TotalLength` is the whole content's length, measured from what's stored.
- **A range that runs past the end is cut at the end,** and `part.Range` shows the cut. A range that starts past the
  end throws `ResourceRangeNotSatisfiableException`, whose `ContentLength` is the content's length, and which the
  [exception handler](aspnet-core/status-codes.md) answers with 416.
- **Only the range is read from storage.** With SQL Server, reading the last MiB of a large file costs about what
  reading a 1 MiB file does.
- **To download a resource in parts,** pass the version ID from the first part's `ResourceInfo` to the rest, so a
  newer upload can't mix two versions in one file.

Every download's result has `TotalLength`; `Range` is `null` when it holds the whole content. To serve ranges over
HTTP, see [Range requests](aspnet-core/serving-downloads.md#range-requests).
