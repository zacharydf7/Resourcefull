using System.IO.Compression;
using System.Text.Json;

namespace Resourcefull;

/// <summary>What <see cref="IResourceServiceClient.ExportContainerAsync"/> wrote. <para>Coming in 3.2.</para></summary>
/// <param name="ResourceCount">How many resources (names) it exported.</param>
/// <param name="VersionCount">How many versions it exported, current and older.</param>
/// <param name="ContentSize">The size of the distinct content it wrote, before compression; content that versions share counts once.</param>
public sealed record ResourceExportResult(long ResourceCount, long VersionCount, ResourceSize ContentSize);

/// <summary>What <see cref="IResourceServiceClient.ImportContainerAsync"/> stored. <para>Coming in 3.2.</para></summary>
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

    /// <summary>An export's description of the container, which <see cref="ReadManifestAsync"/> reads.</summary>
    internal sealed record Manifest(int Format, IDictionary<string, string> Metadata, List<ManifestResource> Resources);

    internal sealed record ManifestResource(string Name, List<ManifestVersion> Versions);

    /// <summary>A version as the export describes it; <c>Content</c> names the entry that holds its content.</summary>
    internal sealed record ManifestVersion(string VersionId, DateTimeOffset LastModified, string? ContentType, long ContentLength,
        string ContentHash, IDictionary<string, string> Metadata, ResourceOrigin? CopiedFrom, DateTimeOffset? ExpiresAt, string Content);

    public static async Task<ResourceExportResult> ExportAsync(IResourceRepository repository, ResourceContainerInfo container,
        Stream destination, CancellationToken cancellationToken)
    {
        var resources = new List<ManifestResource>();
        // Each distinct content's entry, by the version's recorded hash, or by its version ID when it has none.
        var written = new Dictionary<string, WrittenContent>();
        long versionCount = 0, contentSize = 0;

        var output = new AsyncWriteStream(destination);
        await using (ZipArchive zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: null, cancellationToken))
        {
            foreach (ResourceInfo current in (await repository.GetAllResourceInfoAsync(container.Name, cancellationToken))
                .OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                var versions = new List<ManifestVersion>();
                List<ResourceInfo> history = await repository.GetResourceVersionsAsync(container.Name, current.Name, cancellationToken);
                // Oldest first, the order an import stores them in, which leaves the newest current as it was.
                history.Reverse();
                foreach (ResourceInfo version in history)
                {
                    string entryName = "content/" + (version.ContentHash is { } hash && hash.StartsWith(HashingStream.HashPrefix, StringComparison.Ordinal)
                        ? hash[HashingStream.HashPrefix.Length..]
                        : version.VersionId);
                    if (!written.TryGetValue(entryName, out WrittenContent? content))
                    {
                        await using Stream? source = await repository.GetResourceStreamAsync(container.Name, current.Name,
                            version.VersionId, cancellationToken);
                        // Deleted while the container was exported.
                        if (source is null) continue;

                        using var hashing = new HashingStream(source);
                        await using (Stream target = await zip.CreateEntry(entryName, CompressionLevel.Fastest).OpenAsync(cancellationToken))
                            await hashing.CopyToAsync(target, cancellationToken);
                        // Measured, rather than the recorded length, which an early release may have taken from its caller.
                        content = hashing.Complete(null, version.LastModified);
                        if (version.ContentHash is not null && content.ContentHash != version.ContentHash)
                        {
                            throw new InvalidDataException(
                                $"Version {version.VersionId} of {current.Name} doesn't match its recorded hash, so the stored content has changed since it was written.");
                        }

                        written[entryName] = content;
                        contentSize += content.ContentLength;
                    }

                    versions.Add(new ManifestVersion(version.VersionId, version.LastModified, version.ContentType, content.ContentLength,
                        content.ContentHash, version.Metadata, version.CopiedFrom, version.ExpiresAt, entryName));
                }

                if (versions.Count == 0) continue;
                resources.Add(new ManifestResource(current.Name, versions));
                versionCount += versions.Count;
            }

            // Last, so it describes only what was written.
            await using Stream manifest = await zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal).OpenAsync(cancellationToken);
            await JsonSerializer.SerializeAsync(manifest, new Manifest(Format, container.Metadata, resources), Json, cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        return new ResourceExportResult(resources.Count, versionCount, contentSize);
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
                    // Not declared, so content changed to another length is caught by the hash check below, as any change is.
                    Metadata = version.Metadata,
                    CopiedFrom = version.CopiedFrom,
                    ExpiresAt = version.ExpiresAt,
                    // Stored too, for applications on 2.x, which read it from storage.
                    Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(containerName, resource.Name, version.VersionId))
                };

                await using Stream content = await entry.OpenAsync(cancellationToken);
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
        foreach (ManifestResource resource in manifest.Resources)
        {
            if (!ResourceNames.IsValidResourceName(resource.Name) || !names.Add(resource.Name))
                throw new InvalidDataException($"The export names a resource \"{resource.Name}\" twice, or with a name that breaks the name rules.");

            var versionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ManifestVersion version in resource.Versions)
            {
                if (version.VersionId.Length is 0 || version.VersionId.Length > ResourceNames.MaxVersionIdLength
                    || !versionIds.Add(version.VersionId) || version.Content.Length is 0 || version.ContentHash.Length is 0)
                {
                    throw new InvalidDataException($"The export describes a version of {resource.Name} that can't be imported: " +
                        "a version ID that's empty, too long or repeated, or no content or hash.");
                }
            }
        }

        return manifest;
    }
}
