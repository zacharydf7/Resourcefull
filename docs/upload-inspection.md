# Inspecting uploads

Files that come from users can be harmful: a document that carries malware, or a web page that calls itself a
picture. An upload inspector looks at each upload's content before it's stored, and refuses what it mustn't keep. A
refused upload stores nothing, and the caller gets a `ResourceContentRefusedException` that says why.

Write an inspector by implementing `IResourceUploadInspector`, and register it:

```csharp
public sealed class PdfOnlyInspector : IResourceUploadInspector
{
    public async ValueTask<ResourceInspectionResult> InspectAsync(ResourceUploadInspection upload,
        CancellationToken cancellationToken)
    {
        // Every PDF file starts with these five bytes.
        byte[] start = new byte[5];
        int read = await upload.Content.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false,
            cancellationToken);
        return start.AsSpan(0, read).SequenceEqual("%PDF-"u8)
            ? ResourceInspectionResult.Accepted
            : ResourceInspectionResult.Refused("The file isn't a PDF.");
    }
}

builder.Services.AddResourcefull(options => options
    .UseSqlServer(connectionString)
    .UseUploadInspector<PdfOnlyInspector>());
```

The inspector is a scoped service, so it can depend on your other services, such as a client for your malware
scanner.

## What the inspector sees

`ResourceUploadInspection` describes the upload:

| Property | What it is |
|---|---|
| `ContainerName`, `ResourceName` | where the upload is going |
| `VersionId` | the ID the new version will have if it's stored, for matching your scanner's logs to stored versions |
| `ContentType` | the type the uploader declared, which nothing has checked against the content |
| `ContentLength` | the length the uploader declared, or `null` if they didn't |
| `Metadata` | the upload's metadata |
| `Content` | the content, as it arrives |

`Content` can only be read forward, once. Read as much of it as the inspection needs: the first few bytes are enough
to check a file's type, and a malware scan reads all of it. The upload doesn't wait for the inspector to read what
it doesn't need.

## How an upload is inspected

The inspector reads the content while the storage provider stores it, so the file is read from the request once, and
never held in memory whole, however large it is. The two keep pace with each other: neither gets more than a MiB
ahead of the other. So a slow scanner slows the upload, rather than making the application hold the file while the
scanner catches up.

The new version stays out of sight until the inspector answers. If the provider finishes storing the content first,
the upload waits for the answer before it makes the version current:

| The inspector | The upload |
|---|---|
| returns `ResourceInspectionResult.Accepted` | is stored as usual |
| returns `ResourceInspectionResult.Refused(reason)` | fails with `ResourceContentRefusedException`, whose `Reason` is the reason given, and stores nothing |
| throws, such as when the scanner can't be reached | fails with the inspector's exception, and stores nothing |
| is still running when the upload is canceled or fails for another reason | the inspector's cancellation token is canceled, and the upload waits for the inspector to return before it reports its own failure |

A refusal comes as soon as the inspector gives it: an inspector that refuses after reading a file's first bytes ends
the upload at once, without the rest being read.

Failing when the inspector throws keeps unscanned files out: if your scanner is down, uploads fail until it's back.
To store files anyway while it's down, catch the failure in your inspector and return `Accepted`, and keep a record
of what to scan later.

With SQL Server, nothing in the database waits on the inspector: the content is stored in batches that are each
committed, and no lock is held while the upload waits for the answer.

### What's inspected

- **Uploads:** `UploadAsync`, both overloads, and `UploadFromRequestAsync` in ASP.NET Core.
- **Imports:** every version `ImportContainerAsync` stores, since an export file can come from anywhere. A refusal
  fails the import, which deletes what it had imported.
- **Not copies, moves or restores.** Their content is already stored, and was inspected when it was uploaded.
- **Not metadata or content type changes.** `SetContentTypeAsync` changes a stored version's type without reading its
  content, so check the new type yourself if your inspector checks that files are the type they claim. Metadata set
  later with `SetResourceMetadataAsync` isn't checked either.

Only one inspector is registered at a time: `UseUploadInspector` replaces any registered before. To run more than one
check, such as a type check and a malware scan, run both in one inspector, and keep in mind that `Content` can only be
read once.

### In ASP.NET Core

[`AddResourcefullExceptionHandler`](aspnet-core/status-codes.md) turns `ResourceContentRefusedException` into 422
Unprocessable Content. The response's title is the exception's message, which ends with your reason, so the uploader
sees it: say what's wrong with the file, such as "The file contains malware.", but nothing they mustn't know,
such as which scanner you use or its version.

## Scanning for malware with ClamAV

[ClamAV](https://www.clamav.net/) is a free, open-source virus scanner. Its daemon, `clamd`, scans a stream sent to
it over TCP with its `INSTREAM` command, so the file never has to be written to disk. The inspector below sends the
content in chunks, each preceded by its length as a 4-byte big-endian number, then a zero length to mark the end, and reads the
answer: `stream: OK`, or `stream: <signature> FOUND`.

```csharp
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

public sealed class ClamAvInspector : IResourceUploadInspector
{
    public async ValueTask<ResourceInspectionResult> InspectAsync(ResourceUploadInspection upload,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("clamav", 3310, cancellationToken);
        await using NetworkStream clamd = client.GetStream();

        await clamd.WriteAsync("zINSTREAM\0"u8.ToArray(), cancellationToken);
        byte[] buffer = new byte[64 * 1024];
        byte[] length = new byte[4];
        int read;
        while ((read = await upload.Content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, read);
            await clamd.WriteAsync(length, cancellationToken);
            await clamd.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        BinaryPrimitives.WriteInt32BigEndian(length, 0);
        await clamd.WriteAsync(length, cancellationToken);

        // clamd answers, then closes the connection.
        string answer = (await new StreamReader(clamd, Encoding.ASCII).ReadToEndAsync(cancellationToken)).TrimEnd('\0');
        if (answer.EndsWith(" OK", StringComparison.Ordinal)) return ResourceInspectionResult.Accepted;
        if (answer.EndsWith(" FOUND", StringComparison.Ordinal))
            return ResourceInspectionResult.Refused("The file contains malware.");
        throw new InvalidOperationException($"ClamAV couldn't scan the file: {answer}");
    }
}
```

Before relying on it:

- **Raise clamd's size limits** to your largest upload. In `clamd.conf`, `StreamMaxLength` caps how much clamd
  accepts: past it, clamd answers with an error, so the inspector throws and the upload fails. `MaxFileSize` and
  `MaxScanSize` cap how much it scans.
- **Turn on `AlertExceedsMax`,** so a file too large to scan fully is reported as found, and refused, rather than
  passed as clean.
- **Keep its signatures up to date,** with `freshclam`, as the ClamAV container image does by itself.
- **Read the host and port from configuration,** rather than writing them in the code as this example does.

ClamAV runs well in a container next to your application, such as the `clamav/clamav` image, listening on port 3310.

## Scanning for malware on Windows

On Windows, the [Antimalware Scan Interface](https://learn.microsoft.com/windows/win32/amsi/antimalware-scan-interface-portal)
(AMSI) asks the installed antivirus product, such as Microsoft Defender, to scan content. It scans a buffer, so the
inspector holds the whole file in memory: use it only alongside a [`MaxResourceSize`](container-policies.md) small
enough for that.

```csharp
using System.Runtime.InteropServices;

public sealed class AmsiInspector : IResourceUploadInspector
{
    // AMSI results of 16384 and up mean an administrator's policy blocks the content, or it's malware.
    private const int BlockedOrMalware = 16384;

    public async ValueTask<ResourceInspectionResult> InspectAsync(ResourceUploadInspection upload,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await upload.Content.CopyToAsync(buffer, cancellationToken);
        byte[] content = buffer.ToArray();

        Marshal.ThrowExceptionForHR(AmsiInitialize("MyApplication", out nint context));
        try
        {
            Marshal.ThrowExceptionForHR(AmsiScanBuffer(context, content, (uint)content.Length, upload.ResourceName,
                0, out int result));
            return result >= BlockedOrMalware
                ? ResourceInspectionResult.Refused("The file contains malware.")
                : ResourceInspectionResult.Accepted;
        }
        finally
        {
            AmsiUninitialize(context);
        }
    }

    [DllImport("amsi.dll", CharSet = CharSet.Unicode)]
    private static extern int AmsiInitialize(string appName, out nint context);

    [DllImport("amsi.dll")]
    private static extern void AmsiUninitialize(nint context);

    [DllImport("amsi.dll", CharSet = CharSet.Unicode)]
    private static extern int AmsiScanBuffer(nint context, byte[] buffer, uint length, string contentName,
        nint session, out int result);
}
```

AMSI is only as good as the antivirus product behind it, and finds nothing on a machine without one that supports
it. Check that yours does, for example by uploading the harmless
[EICAR test file](https://www.eicar.org/download-anti-malware-testfile/), which antivirus products report as malware.

## Checking what a file really is

The type an upload declares is only what the uploader says. [`AllowedContentTypes`](container-policies.md) checks
that declared type, before any content is read; an inspector can check that the content matches it. Most file formats
start with a fixed signature:

```csharp
public sealed class SignatureInspector : IResourceUploadInspector
{
    private static readonly Dictionary<string, byte[]> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = "%PDF-"u8.ToArray(),
        ["image/png"] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
        ["image/jpeg"] = [0xFF, 0xD8, 0xFF],
        ["image/gif"] = "GIF8"u8.ToArray()
    };

    public async ValueTask<ResourceInspectionResult> InspectAsync(ResourceUploadInspection upload,
        CancellationToken cancellationToken)
    {
        if (upload.ContentType is null || !Signatures.TryGetValue(upload.ContentType, out byte[]? signature))
            return ResourceInspectionResult.Refused("Only PDF, PNG, JPEG and GIF files can be uploaded.");

        byte[] start = new byte[signature.Length];
        int read = await upload.Content.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false,
            cancellationToken);
        return start.AsSpan(0, read).SequenceEqual(signature)
            ? ResourceInspectionResult.Accepted
            : ResourceInspectionResult.Refused($"The file isn't the {upload.ContentType} it says it is.");
    }
}
```

A matching signature shows the file is the type it claims, not that it's harmless: a valid PDF can still carry
malware. Check the type and scan for malware when files come from people you don't trust.

## With a provider of your own

The upload waits for the inspector's answer when the provider reads the content to its end, so a
[provider of your own](providers/writing-a-provider.md) must read until a read returns nothing before it makes the
version current, and store nothing if that read throws. Every built-in provider does, and the
[provider tests](providers/writing-a-provider.md#testing-a-provider) check it. A provider that stops reading sooner
makes the version current before the answer; Resourcefull then deletes it if the inspector refuses it or throws,
but until then it can be read. An inspected upload's stream can't seek, so a provider can't retry it by reading the
content again.

## Telemetry

Each inspection records a `resourcefull inspect` span, under the upload's or import's, with
`resourcefull.inspection_outcome`: `accepted`, `refused`, `failed` when the inspector threw, or `canceled` when the
upload failed first. The `resourcefull.client.inspection.duration` histogram records how long the inspector took to
answer, with the same outcome. A refused upload's own span fails with `ResourceContentRefusedException`. See
[Telemetry](telemetry.md).

Watch for `failed` inspections, which mean uploads are failing because the scanner is, and for inspections that take
longer over time, which slow every upload.
