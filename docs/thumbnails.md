# Thumbnails and previews

<div class="rf-version"><span class="rf-badge rf-upcoming">Coming in 4.0</span></div>

Pages that list photos or documents usually show a small preview of each, not the full file. Resourcefull doesn't make
previews itself, because that takes an image library, and which one to use, under which license, is each
application's choice. This page shows how to make one with the library you use, store it, and keep it together with
the original.

The examples use [ImageSharp](https://github.com/SixLabors/ImageSharp), which since version 3 is under the Six Labors
Split License: free for open-source projects and for companies with under US$1 million in annual revenue, and paid
otherwise. Check its terms for yours. [SkiaSharp](https://github.com/mono/SkiaSharp) and
[Magick.NET](https://github.com/dlemstra/Magick.NET) work the same way, with their own calls in place of the
ImageSharp ones.

## Make the thumbnail after the upload

Upload the original first, then read it back and store a smaller copy in a container of thumbnails. Name each
thumbnail after the original's version ID, so each version has its own and a thumbnail never changes once it's made:

```csharp
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

ResourceInfo photo = await photos.UploadAsync(name, content, new ResourceUploadOptions
{
    Headers = new ResourceHeaders(contentType, null)
}, cancellationToken);

try
{
    // Read the size from the image's header first: a small file can claim to be huge, and decoding it would use that
    // much memory.
    await using (ResourceDownloadResult header = (await photos.DownloadStreamingAsync(name, photo.VersionId, cancellationToken: cancellationToken))!)
    {
        ImageInfo info = await Image.IdentifyAsync(header.Stream, cancellationToken);
        if ((long)info.Width * info.Height > 50_000_000) return photo;   // over 50 megapixels, about 200 MB decoded
    }

    await using ResourceDownloadResult download = (await photos.DownloadStreamingAsync(name, photo.VersionId, cancellationToken: cancellationToken))!;
    using Image image = await Image.LoadAsync(download.Stream, cancellationToken);
    image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(320, 320), Mode = ResizeMode.Max }));

    var thumbnail = new MemoryStream();
    await image.SaveAsWebpAsync(thumbnail, cancellationToken);
    thumbnail.Position = 0;
    await thumbnails.UploadAsync($"{photo.VersionId}.webp", thumbnail, new ResourceUploadOptions
    {
        Headers = new ResourceHeaders("image/webp", thumbnail.Length),
        Metadata = new Dictionary<string, string> { ["photo"] = name },
        ExpiresAt = photo.ExpiresAt,
        Mode = ResourceWriteMode.Replace
    }, cancellationToken);
}
catch (ImageFormatException)
{
    // Not an image ImageSharp can read, or a damaged one: the photo is stored, just without a thumbnail.
}

return photo;
```

- **Check the size before decoding.** Decoding an image takes memory for every pixel, so a file of a few kilobytes
  that claims to be 50,000 pixels square would take gigabytes. Reading only the header first lets you refuse it. An
  [upload inspector](upload-inspection.md) can do the same check, and refuse the upload itself.
- **Not every upload is an image the library can read.** ImageSharp throws an `ImageFormatException` for a format it
  doesn't know or a damaged file, so catch it and go on without a thumbnail, rather than fail after the original is
  stored.
- **Keep thumbnails in a container of their own.** Then nothing that lists or sweeps the photos' container, such as
  the orphan sweep in [Files and your own data](files-and-your-data.md#deleting-orphans), mistakes them for photos.
- **Record the photo's name** in the thumbnail's metadata, so you can find the photo from its thumbnail.
- **Give the thumbnail the original's expiry,** if it has one, so it doesn't outlive the photo.
- **`Replace`** lets a retry replace a thumbnail an earlier attempt made.

Making a thumbnail of a large photo can take a second or more. If uploads shouldn't wait for that, put the version on
a queue, such as a `Channel<T>` read by a `BackgroundService`, and make the thumbnail there. Until it's made, show a
placeholder. A queue in memory loses what's on it when the application restarts, so a job that makes any missing
thumbnails catches those.

## Serve it

Because a thumbnail's name includes the version ID and never changes, browsers and CDNs can keep it for a long time:

```csharp
app.MapGet("/photos/{versionId}/thumbnail", (string versionId, IResourceServiceClient resources) =>
    Results.Resource(resources.GetContainerClient("thumbnails"), $"{versionId}.webp",
        options: new ResourceResultOptions { Inline = true, CacheControl = "public, max-age=31536000, immutable" }));
```

An `<img>` tag shows the thumbnail whatever the response says about downloading. `Inline` matters when someone opens
the URL directly: WebP is one of the types it displays, so the browser shows the image instead of saving it.

Use `public` only if anyone may see the photo. Otherwise use `private, max-age=31536000, immutable`, so only the user's
own browser keeps a copy, and check access in the endpoint, as [Securing file endpoints](aspnet-core/securing-endpoints.md)
describes.

## Keep it with the original

A thumbnail is a resource of its own, so Resourcefull doesn't delete or copy it along with the photo:

- **When you delete a photo,** delete its thumbnails too: one for each version you delete.
- **With [versioning](versioning.md),** each version has its own thumbnail. Pruning doesn't delete them, so a job that
  runs regularly should delete the thumbnails whose versions are gone: list the thumbnails container, and for each
  thumbnail call `GetResourceInfoAsync` on the photos container, with its `photo` metadata as the name and the version
  ID from its own name. If that returns null, the version is gone, so delete the thumbnail.
- **Copies and moves** take only the photo. A copy is a new version with a new ID, so make its thumbnail as for an
  upload. A move keeps its versions' IDs, so their thumbnails still match, but set each thumbnail's `photo` metadata
  to the new name with `SetResourceMetadataAsync`, or the cleanup job above will take the moved photo's versions for
  gone and delete their thumbnails.

## Previews of other files

The same pattern works for other kinds of files: render a PDF's first page, or take a frame from a video, with the
library or tool you use for that, and store the image as above.
