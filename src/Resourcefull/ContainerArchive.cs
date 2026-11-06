using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>
/// What <see cref="IResourceServiceClient.ExportContainerAsync"/> wrote.
/// </summary>
public sealed record ContainerExportResult
{
    /// <summary>How many resources (names) were exported.</summary>
    public required long ResourceCount { get; init; }

    /// <summary>How many versions were exported, current and older.</summary>
    public required long VersionCount { get; init; }

    /// <summary>
    /// The size of the distinct content written, before compression; content that versions share counts once.
    /// </summary>
    public required ResourceSize ContentSize { get; init; }
}

/// <summary>
/// What an import stored: <see cref="IResourceServiceClient.ImportContainerAsync(string, Stream, CancellationToken)"/>, or
/// another import of versions.
/// </summary>
public sealed record ContainerImportResult
{
    /// <summary>How many resources (names) were imported.</summary>
    public required long ResourceCount { get; init; }

    /// <summary>How many versions were imported, current and older.</summary>
    public required long VersionCount { get; init; }

    /// <summary>The size of every version stored, as the container now holds them.</summary>
    public required ResourceSize ContentSize { get; init; }
}

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
        [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string> Metadata,
        [property: JsonPropertyName("copiedFrom")] ManifestOrigin? CopiedFrom,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("retainUntil"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RetainUntil = null,
        [property: JsonPropertyName("legalHold"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool LegalHold = false);

    /// <summary>
    /// A <see cref="ResourceOrigin"/> as the export describes it, so the public type can change without the format.
    /// </summary>
    internal sealed record ManifestOrigin(
        [property: JsonPropertyName("containerName")] string ContainerName,
        [property: JsonPropertyName("resourceName")] string ResourceName,
        [property: JsonPropertyName("versionId")] string VersionId);

    /// <summary>How many names an export lists the versions of at a time.</summary>
    internal const int NamesPerPage = 1_000;

    public static async Task<ContainerExportResult> ExportAsync(ResourceRepository repository, ContainerInfo container,
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
        var manifestFile = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), manifestOptions);
        await using var manifestFileDisposal = manifestFile.ConfigureAwait(false);
        var manifest = new Utf8JsonWriter(manifestFile, new JsonWriterOptions { Indented = true });
        await using var manifestDisposal = manifest.ConfigureAwait(false);
        manifest.WriteStartObject();
        manifest.WriteNumber("format", Format);
        manifest.WritePropertyName("metadata");
        JsonSerializer.Serialize(manifest, container.Metadata, Json);
        manifest.WriteStartArray("resources");

        var output = new AsyncWriteStream(destination);
        ZipArchive zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null,
            cancellationToken).ConfigureAwait(false);
        await using (zip.ConfigureAwait(false))
        {
            string? afterName = null;
            while (true)
            {
                IReadOnlyList<ResourceInfo> page = await repository.GetVersionPageAsync(container.Name, afterName, NamesPerPage,
                    cancellationToken).ConfigureAwait(false);
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
                            Stream? source = await repository.GetResourceStreamAsync(
                                new ResourceAddress(container.Name, name.Key), version.VersionId, cancellationToken).ConfigureAwait(false);
                            // Deleted while the container was exported.
                            if (source is null) continue;
                            await using var sourceDisposal = source.ConfigureAwait(false);

                            using var hashing = new HashingStream(source);
                            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                            Stream target = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                            await using (target.ConfigureAwait(false))
                                await hashing.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
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
                            entryName, version.RetainUntil, version.LegalHold));
                    }

                    if (versions.Count == 0) continue;
                    JsonSerializer.Serialize(manifest, new ManifestResource(name.Key, versions), Json);
                    await manifest.FlushAsync(cancellationToken).ConfigureAwait(false);
                    resourceCount++;
                    versionCount += versions.Count;
                }
            }

            manifest.WriteEndArray();
            manifest.WriteEndObject();
            await manifest.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Last, so it describes only what was written.
            manifestFile.Position = 0;
            ZipArchiveEntry manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            Stream manifestTarget = await manifestEntry.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var manifestTargetDisposal = manifestTarget.ConfigureAwait(false);
            await manifestFile.CopyToAsync(manifestTarget, cancellationToken).ConfigureAwait(false);
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        activity?.SetTag("resourcefull.pages", pages);
        return new ContainerExportResult
        {
            ResourceCount = resourceCount,
            VersionCount = versionCount,
            ContentSize = contentSize
        };
    }

    /// <summary>
    /// Throws before anything is stored if the export holds what the provider can't keep: an expiring version when it
    /// doesn't support expiry, a retained or held version when it doesn't support retention, or a value it can't index
    /// for search.
    /// </summary>
    public static void ThrowIfUnimportable(Manifest manifest, ResourceRepository repository, ResourcefullOptions options)
    {
        if (!repository.SupportsExpiry && manifest.Resources.SelectMany(r => r.Versions).Any(v => v.ExpiresAt is not null))
            throw new NotSupportedException($"{repository.GetType().Name} doesn't support expiry, which the export's versions have.");
        if (!repository.SupportsRetention && manifest.Resources.SelectMany(r => r.Versions).Any(v => v.RetainUntil is not null || v.LegalHold))
            throw new NotSupportedException($"{repository.GetType().Name} doesn't support retention, which the export's versions have.");
        foreach (ManifestVersion version in manifest.Resources.SelectMany(r => r.Versions))
            options.ThrowIfUnsearchable(version.Metadata, "source");
    }

    /// <summary>Every version an export describes, in its order, each opening its content from the zip.</summary>
    public static IEnumerable<ImportedVersion> VersionsOf(ZipArchive zip, Manifest manifest)
    {
        foreach (ManifestResource resource in manifest.Resources)
        {
            foreach (ManifestVersion version in resource.Versions)
            {
                ZipArchiveEntry entry = zip.GetEntry(version.Content)
                    ?? throw new InvalidDataException($"The export has no content for version {version.VersionId} of {resource.Name}.");
                var info = new ResourceInfo
                {
                    ContainerName = "",
                    Name = resource.Name,
                    VersionId = version.VersionId,
                    LastModified = version.LastModified,
                    ContentType = version.ContentType,
                    ContentLength = version.ContentLength,
                    ContentHash = version.ContentHash,
                    Metadata = version.Metadata,
                    CopiedFrom = version.CopiedFrom is { } from ? new ResourceOrigin(from.ContainerName, from.ResourceName, from.VersionId) : null,
                    ExpiresAt = version.ExpiresAt,
                    RetainUntil = version.RetainUntil,
                    LegalHold = version.LegalHold
                };
                // Able to go back to its start, so a provider that retries a transient failure can read it again, unless
                // an upload inspector reads it too: the content is then read once.
                yield return new ImportedVersion { Version = info, OpenContentAsync = async ct =>
                    new RewindableEntryStream(entry, await entry.OpenAsync(ct).ConfigureAwait(false), version.ContentLength) };
            }
        }
    }

    /// <summary>
    /// Stores <paramref name="versions"/> in <paramref name="containerName"/>, which must exist, each as it was, in the
    /// order given, so each name's newest ends up current. With <paramref name="newContainer"/>, the container is one the
    /// import just created, so its usage is counted here rather than read before each version a policy limits. Error
    /// messages name what the versions come from as <paramref name="source"/>, such as "the export".
    /// </summary>
    public static async Task<ContainerImportResult> ImportAsync(IAsyncEnumerable<ImportedVersion> versions, string containerName,
        bool newContainer, string source, ResourceRepository repository, ResourcefullOptions options, ContainerPolicies? policies,
        ClientTelemetry telemetry, IResourceUploadInspector? inspector, CancellationToken cancellationToken)
    {
        // In a new container, retention and holds go on once every version is stored, so an import that fails can still
        // delete what it stored. In one that exists, the versions stored before a failure stay, so each version gets them
        // as it's stored.
        var protectedVersions = new List<(string Name, ResourceInfo Version)>();
        long versionCount = 0, contentSize = 0;
        // A set, so a name whose versions aren't next to each other in the stream is still counted once, and a policy's
        // count limit sees the right number.
        var names = new HashSet<string>(StringComparer.Ordinal);
        await foreach (ImportedVersion imported in versions.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ResourceInfo version = imported.Version;
            if (version.ExpiresAt is not null && !repository.SupportsExpiry)
                throw new NotSupportedException($"{repository.GetType().Name} doesn't support expiry, which {source}'s versions have.");
            bool protects = version.RetainUntil is not null || version.LegalHold;
            if (protects && !repository.SupportsRetention)
                throw new NotSupportedException($"{repository.GetType().Name} doesn't support retention, which {source}'s versions have.");
            options.ThrowIfUnsearchable(version.Metadata, "source");

            Stream? opened = await imported.OpenContentAsync(cancellationToken).ConfigureAwait(false);
            // Gone from the source since it was listed.
            if (opened is null) continue;
            await using var openedDisposal = opened.ConfigureAwait(false);
            var info = new NewResourceVersion(new ResourceAddress(containerName, version.Name), version.VersionId)
            {
                LastModified = version.LastModified,
                ContentType = version.ContentType,
                // Not declared: the hash check below catches content of another length.
                Metadata = version.Metadata,
                CopiedFrom = version.CopiedFrom,
                ExpiresAt = version.ExpiresAt
            };

            // A new container's usage is what the import has stored so far; a name is counted once its first version is
            // stored, and a policy's count limit lets a name it already has take more versions.
            ContainerUsage? usage = newContainer ? new ContainerUsage
            {
                StoredSize = contentSize,
                ResourceCount = names.Count,
                VersionCount = versionCount
            } : null;
            Stream checkedContent = policies is not null && await policies.CheckNewVersionAsync(containerName, version.Name, restore: false,
                version.ContentType, version.ContentLength, telemetry, cancellationToken, usage).ConfigureAwait(false) is { Limit: { } limit }
                ? new LimitedContentStream(opened, limit)
                : opened;
            // Disposed in a finally rather than with await using, since ConfigureAwait's wrapper can't dispose null.
            InspectedContentStream? inspected = inspector is null
                ? null
                : InspectedContentStream.Start(inspector, info, version.ContentLength, checkedContent, telemetry, "import",
                    cancellationToken);
            WrittenContent stored;
            try
            {
                stored = await repository.ImportVersionAsync(info, inspected ?? checkedContent, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (inspected is not null) await inspected.ThrowIfStoredUnansweredAsync(cancellationToken).ConfigureAwait(false);
                    if (version.ContentHash is { } expected && stored.ContentHash != expected)
                    {
                        throw new InvalidDataException(
                            $"The content of version {version.VersionId} of {version.Name} doesn't match its hash: {source} was damaged or changed.");
                    }
                }
                catch when (!newContainer)
                {
                    // A refused version, or one whose content doesn't match its hash, fails the import. A new container
                    // is then deleted with everything in it; in one that exists, only this version is deleted, as a
                    // refused upload's is.
                    await repository.DeleteResourceAsync(info.Resource, info.VersionId, null, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                if (inspected is not null) await inspected.DisposeAsync().ConfigureAwait(false);
            }

            telemetry.RecordUploaded("import", stored.ContentLength);
            names.Add(version.Name);
            versionCount++;
            contentSize += stored.ContentLength;
            if (!protects) continue;
            if (newContainer) protectedVersions.Add((version.Name, version));
            else await ProtectAsync(repository, info.Resource, version, cancellationToken).ConfigureAwait(false);
        }

        foreach ((string name, ResourceInfo version) in protectedVersions)
            await ProtectAsync(repository, new ResourceAddress(containerName, name), version, cancellationToken).ConfigureAwait(false);

        return new ContainerImportResult
        {
            ResourceCount = names.Count,
            VersionCount = versionCount,
            ContentSize = contentSize
        };
    }

    /// <summary>Gives an imported version the retention date and hold it had.</summary>
    private static async Task ProtectAsync(ResourceRepository repository, ResourceAddress location, ResourceInfo version,
        CancellationToken cancellationToken)
    {
        if (version.RetainUntil is { } retainUntil)
            await repository.SetRetentionAsync(location, version.VersionId, retainUntil, cancellationToken).ConfigureAwait(false);
        if (version.LegalHold)
            await repository.SetLegalHoldAsync(location, version.VersionId, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads an export's manifest, checking that it is one this release can read.</summary>
    /// <exception cref="InvalidDataException">The archive isn't a container export this release can read.</exception>
    public static async Task<Manifest> ReadManifestAsync(ZipArchive zip, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = zip.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException("This isn't a container export: it has no manifest.json.");
        Manifest? manifest;
        Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream, Json, cancellationToken).ConfigureAwait(false);
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
            if (!ResourceNames.IsValidStoredResourceName(resource.Name) || !names.Add(resource.Name))
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
        _content ??= await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        return Counted(await _content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
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
        if (_content is not null) await _content.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
