using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resourcefull;

/// <summary>
/// What <see cref="IResourceServiceClient.ExportContainerAsync"/> wrote.
/// <para>Coming in 3.2.</para>
/// </summary>
/// <param name="ResourceCount">How many resources (names) it exported.</param>
/// <param name="VersionCount">How many versions it exported, current and older.</param>
/// <param name="ContentSize">The size of the distinct content it wrote, before compression; content that versions share counts once.</param>
public sealed record ResourceExportResult(long ResourceCount, long VersionCount, ResourceSize ContentSize);

/// <summary>
/// What <see cref="IResourceServiceClient.ImportContainerAsync"/> stored.
/// <para>Coming in 3.2.</para>
/// </summary>
/// <param name="ResourceCount">How many resources (names) it imported.</param>
/// <param name="VersionCount">How many versions it imported, current and older.</param>
/// <param name="ContentSize">The size of every version it stored, as the container now holds them.</param>
public sealed record ResourceImportResult(long ResourceCount, long VersionCount, ResourceSize ContentSize);

/// <summary>
/// A container as a zip: <c>manifest.json</c>, describing the container and every version of every resource in the
/// order they were stored, and each distinct content once, under <c>content/</c>.
/// </summary>
internal static class ContainerArchive
{
    private const int Format = 1;
    private const string ManifestEntry = "manifest.json";
    // A manifest that leaves out a field, or makes one null that can't be, is refused rather than read with defaults.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    // The manifest's JSON names are the file format, so each is spelled out: renaming a property mustn't change them.

    /// <summary>An export's description of the container, which <see cref="ReadManifestAsync"/> reads.</summary>
    internal sealed record Manifest(
        [property: JsonPropertyName("format")] int Format,
        [property: JsonPropertyName("metadata")] IDictionary<string, string> Metadata,
        [property: JsonPropertyName("resources")] List<ManifestResource> Resources);

    internal sealed record ManifestResource(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("versions")] List<ManifestVersion> Versions);

    /// <summary>A version as the export describes it; <c>Content</c> names the entry that holds its content.</summary>
    internal sealed record ManifestVersion(
        [property: JsonPropertyName("versionId")] string VersionId,
        [property: JsonPropertyName("lastModified")] DateTimeOffset LastModified,
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("contentLength")] long ContentLength,
        [property: JsonPropertyName("contentHash")] string ContentHash,
        [property: JsonPropertyName("metadata")] IDictionary<string, string> Metadata,
        [property: JsonPropertyName("copiedFrom")] ManifestOrigin? CopiedFrom,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("content")] string Content);

    /// <summary>
    /// A <see cref="ResourceOrigin"/> as the export describes it, so the public type can change without the format.
    /// </summary>
    internal sealed record ManifestOrigin(
        [property: JsonPropertyName("containerName")] string ContainerName,
        [property: JsonPropertyName("resourceName")] string ResourceName,
        [property: JsonPropertyName("versionId")] string VersionId);

    /// <summary>How many names an export lists the versions of at a time.</summary>
    internal const int NamesPerPage = 1_000;

    public static async Task<ResourceExportResult> ExportAsync(IResourceRepository repository, ResourceContainerInfo container,
        Stream destination, Activity? activity, CancellationToken cancellationToken)
    {
        // Each distinct content's entry, by the version's recorded hash, or by its version ID when it has none.
        var written = new Dictionary<string, WrittenContent>();
        long resourceCount = 0, versionCount = 0, contentSize = 0;
        int pages = 0;

        // The manifest goes to a temporary file as the export finds each resource, and is copied into the zip at its
        // end, rather than being held in memory. Only this process's user can read it, since it names every file.
        var manifestOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
            manifestOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var manifestFile = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            manifestOptions);
        await using var manifest = new Utf8JsonWriter(manifestFile, new JsonWriterOptions { Indented = true });
        manifest.WriteStartObject();
        manifest.WriteNumber("format", Format);
        manifest.WritePropertyName("metadata");
        JsonSerializer.Serialize(manifest, container.Metadata, Json);
        manifest.WriteStartArray("resources");

        var output = new AsyncWriteStream(destination);
        await using (ZipArchive zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: null, cancellationToken))
        {
            string? afterName = null;
            while (true)
            {
                List<ResourceInfo> page = await repository.GetVersionPageAsync(container.Name, afterName, NamesPerPage,
                    cancellationToken);
                // Only an empty page ends it: a page can hold fewer names than asked for, as when one is deleted while
                // it's read, and more still follow.
                if (page.Count == 0) break;
                pages++;
                foreach (IGrouping<string, ResourceInfo> name in page.GroupBy(v => v.Name, StringComparer.Ordinal))
                {
                    afterName = name.Key;
                    var versions = new List<ManifestVersion>();
                    // Oldest first, the order an import stores them in, which leaves the newest current as it was.
                    foreach (ResourceInfo version in name.Reverse())
                    {
                        string entryName = "content/" + (version.ContentHash is { } hash
                            && hash.StartsWith(HashingStream.HashPrefix, StringComparison.Ordinal)
                                ? hash[HashingStream.HashPrefix.Length..]
                                : version.VersionId);
                        if (!written.TryGetValue(entryName, out WrittenContent? content))
                        {
                            await using Stream? source = await repository.GetResourceStreamAsync(container.Name,
                                name.Key, version.VersionId, cancellationToken);
                            // Deleted while the container was exported.
                            if (source is null) continue;

                            using var hashing = new HashingStream(source);
                            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                            await using (Stream target = await entry.OpenAsync(cancellationToken))
                                await hashing.CopyToAsync(target, cancellationToken);
                            // Measured, rather than the recorded length, which an early release may have taken from
                            // its caller.
                            content = hashing.Complete(null, version.LastModified);
                            if (version.ContentHash is not null && content.ContentHash != version.ContentHash)
                            {
                                throw new InvalidDataException($"Version {version.VersionId} of {name.Key} doesn't " +
                                    "match its recorded hash, so the stored content has changed since it was written.");
                            }

                            written[entryName] = content;
                            contentSize += content.ContentLength;
                        }

                        ManifestOrigin? origin = version.CopiedFrom is { } from
                            ? new ManifestOrigin(from.ContainerName, from.ResourceName, from.VersionId)
                            : null;
                        versions.Add(new ManifestVersion(version.VersionId, version.LastModified, version.ContentType,
                            content.ContentLength, content.ContentHash, version.Metadata, origin, version.ExpiresAt,
                            entryName));
                    }

                    if (versions.Count == 0) continue;
                    JsonSerializer.Serialize(manifest, new ManifestResource(name.Key, versions), Json);
                    await manifest.FlushAsync(cancellationToken);
                    resourceCount++;
                    versionCount += versions.Count;
                }
            }

            manifest.WriteEndArray();
            manifest.WriteEndObject();
            await manifest.FlushAsync(cancellationToken);

            // Last, so it describes only what was written.
            manifestFile.Position = 0;
            ZipArchiveEntry manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            await using Stream manifestTarget = await manifestEntry.OpenAsync(cancellationToken);
            await manifestFile.CopyToAsync(manifestTarget, cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        activity?.SetTag("resourcefull.pages", pages);
        return new ResourceExportResult(resourceCount, versionCount, contentSize);
    }

    /// <summary>Stores every version an export describes in <paramref name="containerName"/>, which must exist and be empty.</summary>
    public static async Task<ResourceImportResult> ImportAsync(ZipArchive zip, Manifest manifest, string containerName,
        IResourceRepository repository, ContainerPolicies? policies, ClientTelemetry telemetry, IUriProvider? uriProvider,
        CancellationToken cancellationToken)
    {
        // The container is new, so its usage is what the import has stored so far: counted here, so a policy's limits
        // don't add up the container again before each version.
        long versionCount = 0, contentSize = 0, resourceCount = 0;
        foreach (ManifestResource resource in manifest.Resources)
        {
            foreach (ManifestVersion version in resource.Versions)
            {
                ZipArchiveEntry entry = zip.GetEntry(version.Content)
                    ?? throw new InvalidDataException($"The export has no content for version {version.VersionId} of {resource.Name}.");
                var info = new ResourceInfo
                {
                    ContainerName = containerName,
                    Name = resource.Name,
                    VersionId = version.VersionId,
                    LastModified = version.LastModified,
                    ContentType = version.ContentType,
                    // Not declared: the entry's stream refuses content longer than the manifest's length, and the hash
                    // check below catches any other change.
                    Metadata = version.Metadata,
                    CopiedFrom = version.CopiedFrom is { } from
                        ? new ResourceOrigin(from.ContainerName, from.ResourceName, from.VersionId)
                        : null,
                    ExpiresAt = version.ExpiresAt,
                    // Stored too, for applications on 2.x, which read it from storage.
                    Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(containerName, resource.Name, version.VersionId))
                };

                // Able to go back to its start, so a provider that retries a transient failure can read it again.
                await using Stream content = new RewindableEntryStream(entry, await entry.OpenAsync(cancellationToken),
                    version.ContentLength);
                var usage = new ResourceContainerUsage(contentSize, resourceCount, versionCount);
                Stream checkedContent = policies is not null && await policies.CheckNewVersionAsync(containerName, resource.Name, restore: false,
                    version.ContentType, version.ContentLength, telemetry, cancellationToken, usage) is { } limit
                    ? new LimitedContentStream(content, limit)
                    : content;
                WrittenContent stored = await repository.ImportVersionAsync(containerName, info, checkedContent, cancellationToken);
                if (stored.ContentHash != version.ContentHash)
                {
                    throw new InvalidDataException(
                        $"The content of version {version.VersionId} of {resource.Name} doesn't match its hash: the export was damaged or changed.");
                }

                telemetry.RecordUploaded("import", stored.ContentLength);

                versionCount++;
                contentSize += stored.ContentLength;
            }

            // Counted once its versions are stored, so its own later versions don't count it against the limit.
            if (resource.Versions.Count > 0) resourceCount++;
        }

        return new ResourceImportResult(manifest.Resources.Count, versionCount, contentSize);
    }

    /// <summary>Reads an export's manifest, checking that it is one this release can read.</summary>
    /// <exception cref="InvalidDataException">The archive isn't a container export this release can read.</exception>
    public static async Task<Manifest> ReadManifestAsync(ZipArchive zip, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException("This isn't a container export: it has no manifest.json.");
        Manifest? manifest;
        await using (Stream stream = await entry.OpenAsync(cancellationToken))
        {
            try
            {
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, Json, cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The export's manifest.json can't be read: the export is damaged or was changed.", exception);
            }
        }

        if (manifest is not { Format: Format })
        {
            throw new InvalidDataException($"The export is format {manifest?.Format}; this release reads format {Format}.");
        }

        // Everything it names is checked as an upload's would be, before anything is created.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestResource? resource in manifest.Resources)
        {
            // JSON's null is allowed in a list whatever its element type says.
            if (resource is null)
                throw new InvalidDataException("The export's manifest lists a null resource: the export was damaged or changed.");
            if (!ResourceNames.IsValidResourceName(resource.Name) || !names.Add(resource.Name))
                throw new InvalidDataException($"The export names a resource \"{resource.Name}\" twice, or with a name that breaks the name rules.");

            var versionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ManifestVersion? version in resource.Versions)
            {
                if (version is null)
                    throw new InvalidDataException($"The export's manifest lists a null version of {resource.Name}: the export was damaged " +
                        "or changed.");
                if (version.VersionId.Length is 0 || version.VersionId.Length > ResourceNames.MaxVersionIdLength
                    || !versionIds.Add(version.VersionId) || version.Content.Length is 0 || version.ContentHash.Length is 0
                    || version.ContentLength < 0)
                {
                    throw new InvalidDataException($"The export describes a version of {resource.Name} that can't be imported: " +
                        "a version ID that's empty, too long or repeated, no content or hash, or a negative length.");
                }
            }
        }

        return manifest;
    }
}

/// <summary>
/// A zip entry's content that can seek back to its start, by opening the entry again; it can't seek anywhere else. It
/// refuses content longer than <paramref name="declaredLength"/>, the length the export's manifest gives the version,
/// before returning it, so a small, highly compressed entry can't write more than the manifest says.
/// </summary>
internal sealed class RewindableEntryStream(ZipArchiveEntry entry, Stream opened, long declaredLength) : Stream
{
    private Stream? _content = opened;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => entry.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>Goes back to the start; the entry is opened again by the next read.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin != SeekOrigin.Begin || offset != 0)
            throw new NotSupportedException("An export's entry can only go back to its start.");
        if (_position == 0) return 0;
        _content?.Dispose();
        _content = null;
        _position = 0;
        return 0;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => Counted((_content ??= entry.Open()).Read(buffer));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _content ??= await entry.OpenAsync(cancellationToken);
        return Counted(await _content.ReadAsync(buffer, cancellationToken));
    }

    private int Counted(int read)
    {
        _position += read;
        if (_position > declaredLength)
        {
            throw new InvalidDataException($"The export's file {entry.FullName} is longer than the {declaredLength} bytes its " +
                "manifest gives it: the export was damaged or changed.");
        }

        return read;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _content?.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_content is not null) await _content.DisposeAsync();
        await base.DisposeAsync();
    }
}
