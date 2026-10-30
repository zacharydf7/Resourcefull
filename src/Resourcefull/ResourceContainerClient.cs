using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resourcefull;

internal partial class ResourceContainerClient(IResourceRepository resourceRepository, IUriProvider? uriProvider,
    ResourcefullOptions options, ResourceContainerInfo containerInfo, ILogger? logger, ContainerPolicies? policies = null,
    IResourceCleanupHandler? cleanupHandler = null, TimeProvider? timeProvider = null) : IResourceContainerClient
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly ClientTelemetry _telemetry = new(Telemetry.ProviderName(resourceRepository), options);

    public ResourceContainerInfo ResourceContainerInfo { get; } = containerInfo;

    private string ContainerName => ResourceContainerInfo.Name;

    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions = null)
        => UploadAsync(resourceName, content, uploadOptions, CancellationToken.None);

    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken)
        => _telemetry.RunAsync("upload", ContainerName, resourceName, () => UploadCoreAsync(resourceName, content, uploadOptions, cancellationToken),
            "resourcefull.conditional", uploadOptions?.Conditions is { IsEmpty: false });

    private async Task<ResourceInfo> UploadCoreAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ResourceNames.ThrowIfInvalidResourceName(resourceName);

        uploadOptions ??= new ResourceUploadOptions();
        ResourceConditions? conditions = uploadOptions.Conditions is { IsEmpty: false } set ? set : null;
        if (uploadOptions.CreateOnly && conditions is not null)
            throw new ArgumentException(
                $"{nameof(ResourceUploadOptions.CreateOnly)} and {nameof(ResourceUploadOptions.Conditions)} can't both be set: " +
                "a create-only upload requires that the name has no current version, and conditions require one.", nameof(uploadOptions));

        // Without versioning, an upload that may not overwrite is a create-only write; one with conditions replaces
        // the version they match.
        bool createOnly = uploadOptions.CreateOnly
            || (!options.IsVersioningEnabled && !uploadOptions.Overwrite && conditions is null);
        if (policies is not null && await policies.CheckNewVersionAsync(ContainerName, resourceName, restore: false,
            uploadOptions.Headers?.ContentType, uploadOptions.Headers?.ContentLength, _telemetry, cancellationToken) is { } limit)
        {
            content = new LimitedContentStream(content, limit);
        }

        var resourceInfo = new ResourceInfo
        {
            ContainerName = ContainerName,
            Name = resourceName,
            VersionId = Guid.CreateVersion7().ToString(),
            ContentType = uploadOptions.Headers?.ContentType,
            ContentLength = uploadOptions.Headers?.ContentLength,
            Metadata = new Dictionary<string, string>(uploadOptions.Metadata),
            ExpiresAt = uploadOptions.ExpiresAt
        };

        try
        {
            return await SaveNewResourceVersionAsync("upload", content, resourceInfo, createOnly, conditions, cancellationToken);
        }
        catch (ResourceConflictException exception) when (!uploadOptions.CreateOnly)
        {
            throw new ResourceConflictException(
                $"Resource with name {resourceName} already exists. " +
                $"Set {nameof(uploadOptions.Overwrite)} to true in {nameof(ResourceUploadOptions)} to overwrite existing resources.", exception);
        }
    }

    public Task<ResourceInfo> UploadAsync(string resourceName, BinaryData content, ResourceUploadOptions? uploadOptions = null)
        => UploadAsync(resourceName, content, uploadOptions, CancellationToken.None);

    public Task<ResourceInfo> UploadAsync(string resourceName, BinaryData content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        return UploadAsync(resourceName, content.ToStream(), uploadOptions, cancellationToken);
    }

    public Task<ResourceInfo?> RestoreVersionAsync(string resourceName, string versionId)
        => RestoreVersionAsync(resourceName, versionId, CancellationToken.None);

    public Task<ResourceInfo?> RestoreVersionAsync(string resourceName, string versionId, CancellationToken cancellationToken)
        => _telemetry.RunAsync("restore", ContainerName, resourceName, () => RestoreCoreAsync(resourceName, versionId, cancellationToken));

    private async Task<ResourceInfo?> RestoreCoreAsync(string resourceName, string versionId, CancellationToken cancellationToken)
    {
        if (!options.IsVersioningEnabled)
            throw new InvalidOperationException("Versioning must be enabled in order to restore versions.");
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        if (string.IsNullOrEmpty(versionId))
            throw new ArgumentException($"{nameof(versionId)} cannot be empty.", nameof(versionId));

        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (resourceInfo == null) return null;

        await using Stream? stream = await resourceRepository.GetResourceStreamAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (stream == null) return null;
        Stream content = policies is not null && await policies.CheckNewVersionAsync(ContainerName, resourceName, restore: true, null, null,
            _telemetry, cancellationToken) is { } limit ? new LimitedContentStream(stream, limit) : stream;

        var newResourceInfo = new ResourceInfo
        {
            ContainerName = resourceInfo.ContainerName,
            Name = resourceInfo.Name,
            VersionId = Guid.CreateVersion7().ToString(),
            ContentType = resourceInfo.ContentType,
            // Measured like any upload rather than declared: lengths stored by earlier releases came from callers.
            ContentLength = null,
            Metadata = resourceInfo.Metadata,
            CopiedFrom = new ResourceOrigin(ContainerName, resourceName, versionId)
        };

        return await SaveNewResourceVersionAsync("restore", content, newResourceInfo, createOnly: false, conditions: null, cancellationToken);
    }

    private async Task<ResourceInfo> SaveNewResourceVersionAsync(string operation, Stream stream, ResourceInfo resourceInfo,
        bool createOnly, ResourceConditions? conditions, CancellationToken cancellationToken)
    {
        // Stored too, for applications on 2.x, which read it from storage, while they share a database with this release
        // during an upgrade.
        WithUri(resourceInfo);

        // Only a conditional upload takes the newer overload, so a provider written for 3.0 handles every other upload.
        WrittenContent written = conditions is null
            ? await resourceRepository.AddResourceAsync(ContainerName, resourceInfo, stream, createOnly, cancellationToken)
            : await resourceRepository.AddResourceAsync(ContainerName, resourceInfo, stream,
                new ResourceWriteCondition { CreateOnly = createOnly, Conditions = conditions }, cancellationToken);

        _telemetry.RecordUploaded(operation, written.ContentLength);
        return await CompleteNewVersionAsync(resourceInfo, written);
    }

    /// <summary>
    /// Fills in what was stored for a new version, and prunes its name's older versions in whichever container it's in.
    /// </summary>
    private async Task<ResourceInfo> CompleteNewVersionAsync(ResourceInfo resourceInfo, WrittenContent written)
    {
        // Report what was stored rather than what was asked for.
        resourceInfo.ContentLength = written.ContentLength;
        resourceInfo.ContentHash = written.ContentHash;
        resourceInfo.LastModified = written.LastModified;
        resourceInfo.IsLatest = true;

        // The new version is stored, so the upload has succeeded; pruning is cleanup that neither the caller's token
        // nor a failure may turn into a reported failure.
        using Activity? prune = _telemetry.StartSpan("prune", resourceInfo.ContainerName);
        try
        {
            await PruneAsync(resourceInfo.ContainerName, resourceInfo.Name, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _telemetry.RecordFailure(prune, exception);
            // The older versions stay until a later overwriting or versioned upload of the name prunes them; every
            // prune works from the versions as they stand after its own write. Logged, as a prune that keeps failing
            // would otherwise let storage grow unnoticed.
            try
            {
                LogPruneFailed(_logger, exception, resourceInfo.ContainerName, resourceInfo.Name);
            }
            catch
            {
                // A logging provider that throws mustn't fail an upload that was stored either.
            }
        }

        return resourceInfo;
    }

    /// <summary>
    /// Keeps the newest versions in store order. Reading the versions after the write means concurrent writers each
    /// see all of them, so together they never keep more than the limit; without versioning the limit is one, which
    /// is what an overwrite leaves.
    /// </summary>
    private async Task PruneAsync(string containerName, string resourceName, CancellationToken cancellationToken)
    {
        int keep = options.IsVersioningEnabled ? options.MaxVersions : 1;
        List<ResourceInfo> versions = await resourceRepository.GetResourceVersionsAsync(containerName, resourceName, cancellationToken);
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        foreach (ResourceInfo version in versions.Skip(keep))
        {
            // Told first, so a handler that fails leaves the version for a later prune rather than deleted unreported.
            if (cleanupHandler is not null)
                await cleanupHandler.OnCleanupAsync(new ResourceCleanup(version, ResourceCleanupReason.Pruned, now), cancellationToken);
            await resourceRepository.DeleteResourceAsync(containerName, resourceName, version.VersionId, cancellationToken);
        }
    }

    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId = null)
        => DownloadStreamingAsync(resourceName, versionId, CancellationToken.None);

    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
        => _telemetry.RunAsync("download", ContainerName, resourceName, () => OpenAsync(resourceName, versionId, null, cancellationToken));

    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId, ResourceRange range,
        CancellationToken cancellationToken)
        => _telemetry.RunAsync("download", ContainerName, resourceName, () => OpenAsync(resourceName, versionId, range, cancellationToken),
            "resourcefull.range", $"{range.Offset}-{range.Offset + range.Length - 1}");

    private async Task<ResourceDownloadResult?> OpenAsync(string resourceName, string? versionId, ResourceRange? range,
        CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        if (range is { } requested && (requested.Offset < 0 || requested.Length < 0))
            throw new ArgumentOutOfRangeException(nameof(range), requested, "A range can't have a negative offset or length.");

        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (resourceInfo == null) return null;
        // Before the stream opens, so a URI provider that throws can't leave it undisposed.
        WithUri(resourceInfo);

        // Read the version just resolved, not "current" again: an upload in between would pair this info with other content.
        Stream? stream = range is { } r
            ? await resourceRepository.GetResourceStreamAsync(ContainerName, resourceName, resourceInfo.VersionId, r.Offset, r.Length, cancellationToken)
            : await resourceRepository.GetResourceStreamAsync(ContainerName, resourceName, resourceInfo.VersionId, cancellationToken);
        if (stream == null) return null;

        long totalLength = stream.Length;
        if (range is not { } part)
            return new ResourceDownloadResult(resourceInfo, _telemetry.CountDownloaded(stream)) { TotalLength = totalLength };

        if (part.Offset > totalLength)
        {
            await stream.DisposeAsync();
            throw new ArgumentOutOfRangeException(nameof(range), part, $"The range starts past the end of the content, which is {totalLength} bytes long.");
        }

        long length = Math.Min(part.Length ?? long.MaxValue, totalLength - part.Offset);
        return new ResourceDownloadResult(resourceInfo, _telemetry.CountDownloaded(new ResourceRangeStream(stream, length)))
        {
            TotalLength = totalLength,
            Range = new ResourceRange(part.Offset, length)
        };
    }

    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId = null)
        => DownloadContentAsync(resourceName, versionId, CancellationToken.None);

    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
        => _telemetry.RunAsync("download", ContainerName, resourceName, async () =>
        {
            ResourceDownloadResult? result = await OpenAsync(resourceName, versionId, null, cancellationToken);
            if (result is null) return null;

            await using Stream stream = result.Stream;
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            return new ResourceContent(result.ResourceInfo, BinaryData.FromBytes(memoryStream.ToArray()));
        });

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId = null)
        => GetResourceInfoAsync(resourceName, versionId, CancellationToken.None);

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
        => _telemetry.RunAsync("get_info", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
            return resourceInfo is null ? null : WithUri(resourceInfo);
        });

    public Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync()
        => GetResourcesAsync(CancellationToken.None);

    public Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync(CancellationToken cancellationToken)
        => _telemetry.RunAsync<IReadOnlyList<ResourceInfo>>("list", ContainerName, null,
            async () => WithUris(await resourceRepository.GetAllResourceInfoAsync(ContainerName, cancellationToken)));

    public Task<ResourcePage> GetResourcePageAsync(ResourceListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("list_page", ContainerName, null, () => GetPageCoreAsync(options, continuationToken, cancellationToken),
            "resourcefull.page_size", options?.PageSize ?? ResourceListOptions.DefaultPageSize);

    private async Task<ResourcePage> GetPageCoreAsync(ResourceListOptions? options, string? continuationToken, CancellationToken cancellationToken)
    {
        options ??= new ResourceListOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PageSize, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PageSize, ResourceListOptions.MaxPageSize, nameof(options));
        string? prefix = string.IsNullOrEmpty(options.Prefix) ? null : options.Prefix;
        string? afterName = continuationToken is null ? null : ResourcePageToken.Decode(continuationToken, prefix, nameof(continuationToken));

        // One more than a page, to tell whether another page follows without returning an empty last page.
        List<ResourceInfo> resources = await resourceRepository.GetResourcePageAsync(ContainerName, prefix, afterName,
            options.PageSize + 1, cancellationToken);
        string? next = null;
        if (resources.Count > options.PageSize)
        {
            resources.RemoveRange(options.PageSize, resources.Count - options.PageSize);
            next = ResourcePageToken.Encode(prefix, resources[^1].Name);
        }
        return new ResourcePage(WithUris(resources), next);
    }

    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName)
        => GetResourceVersionsAsync(resourceName, CancellationToken.None);

    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName, CancellationToken cancellationToken)
        => _telemetry.RunAsync<IReadOnlyList<ResourceInfo>>("list_versions", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            return WithUris(await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken));
        });

    // URIs come from the URI provider whenever a version is returned, not from storage, so a changed provider or base URL
    // applies to versions stored before the change as well.
    private ResourceInfo WithUri(ResourceInfo resourceInfo)
    {
        resourceInfo.Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(resourceInfo.ContainerName, resourceInfo.Name, resourceInfo.VersionId));
        return resourceInfo;
    }

    private List<ResourceInfo> WithUris(List<ResourceInfo> resources)
    {
        foreach (ResourceInfo resourceInfo in resources) WithUri(resourceInfo);
        return resources;
    }

    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null)
        => DeleteResourceIfExistsAsync(resourceName, versionId, CancellationToken.None);

    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
        => DeleteResourceIfExistsAsync(resourceName, versionId, null, cancellationToken);

    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, ResourceConditions? conditions,
        CancellationToken cancellationToken)
        => _telemetry.RunAsync("delete", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken);
            // Only a conditional delete takes the newer overload, so a provider written for 3.0 handles every other one.
            return conditions is null or { IsEmpty: true }
                ? await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, versionId, cancellationToken)
                : await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, versionId, conditions, cancellationToken);
        }, "resourcefull.conditional", conditions is { IsEmpty: false });

    public Task<ResourceInfo?> CopyResourceAsync(string resourceName, string destinationContainer, string destinationName,
        ResourceCopyOptions? options = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("copy", ContainerName, resourceName,
            () => CopyCoreAsync(resourceName, destinationContainer, destinationName, options ?? new ResourceCopyOptions(), cancellationToken),
            "resourcefull.destination_container", destinationContainer);

    private async Task<ResourceInfo?> CopyCoreAsync(string resourceName, string destinationContainer, string destinationName,
        ResourceCopyOptions copyOptions, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        ResourceNames.ThrowIfInvalidContainerName(destinationContainer);
        ResourceNames.ThrowIfInvalidResourceName(destinationName);
        ResourceConditions? conditions = copyOptions.DestinationConditions is { IsEmpty: false } set ? set : null;
        if (copyOptions.CreateOnly && conditions is not null)
            throw new ArgumentException(
                $"{nameof(ResourceCopyOptions.CreateOnly)} and {nameof(ResourceCopyOptions.DestinationConditions)} can't both be set: " +
                "a create-only copy requires that the destination has no current version, and conditions require one.",
                "options"); // CopyResourceAsync's parameter; nameof(options) here is the client's own options
        // As for an upload: without versioning, a copy that may not overwrite is a create-only write.
        bool createOnly = copyOptions.CreateOnly || (!options.IsVersioningEnabled && !copyOptions.Overwrite && conditions is null);

        ResourceInfo? source = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, copyOptions.VersionId, cancellationToken);
        if (source is null) return null;
        // The stored length stands in for a declared one: the content doesn't pass through here to be measured.
        if (policies is not null)
        {
            if (!await WithStoredLengthAsync(source, cancellationToken)) return null;
            await policies.CheckNewVersionAsync(destinationContainer, destinationName, restore: false, source.ContentType, source.ContentLength,
                _telemetry, cancellationToken);
        }

        ResourceInfo copy = WithUri(new ResourceInfo
        {
            ContainerName = destinationContainer,
            Name = destinationName,
            VersionId = Guid.CreateVersion7().ToString(),
            ContentType = source.ContentType,
            Metadata = new Dictionary<string, string>(source.Metadata),
            CopiedFrom = new ResourceOrigin(ContainerName, resourceName, source.VersionId!),
            ExpiresAt = copyOptions.ExpiresAt
        });
        WrittenContent? written = await resourceRepository.CopyResourceAsync(new ResourceLocation(ContainerName, resourceName),
            source.VersionId, copy, new ResourceWriteCondition { CreateOnly = createOnly, Conditions = conditions }, cancellationToken);
        return written is null ? null : await CompleteNewVersionAsync(copy, written);
    }

    public Task<ResourceInfo?> MoveResourceAsync(string resourceName, string destinationContainer, string destinationName,
        ResourceMoveOptions? options = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("move", ContainerName, resourceName,
            () => MoveCoreAsync(resourceName, destinationContainer, destinationName, options?.SourceConditions, cancellationToken),
            "resourcefull.destination_container", destinationContainer);

    private async Task<ResourceInfo?> MoveCoreAsync(string resourceName, string destinationContainer, string destinationName,
        ResourceConditions? conditions, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        ResourceNames.ThrowIfInvalidContainerName(destinationContainer);
        ResourceNames.ThrowIfInvalidResourceName(destinationName);
        if (destinationContainer == ContainerName && destinationName == resourceName)
            throw new ArgumentException("A resource can't be moved to where it already is.", nameof(destinationName));

        if (policies is not null)
        {
            List<ResourceInfo> versions = await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken);
            if (versions.Count == 0) return null;
            foreach (ResourceInfo version in versions)
            {
                if (!await WithStoredLengthAsync(version, cancellationToken)) return null;
            }

            await policies.CheckMoveAsync(ContainerName, destinationContainer, versions, _telemetry, cancellationToken);
        }

        if (!await resourceRepository.MoveResourceAsync(new ResourceLocation(ContainerName, resourceName),
            new ResourceLocation(destinationContainer, destinationName), conditions is { IsEmpty: false } ? conditions : null, cancellationToken))
        {
            return null;
        }

        ResourceInfo? moved = await resourceRepository.GetResourceInfoAsync(destinationContainer, destinationName, null, cancellationToken);
        return moved is null ? null : WithUri(moved);
    }

    /// <summary>
    /// Sets a version's <see cref="ResourceInfo.ContentLength"/> from its stored content when an early release recorded
    /// none, for the size limits to check: a stream's length is the stored content's, known without reading it.
    /// </summary>
    /// <returns><see langword="false"/> if the version no longer exists.</returns>
    private async Task<bool> WithStoredLengthAsync(ResourceInfo version, CancellationToken cancellationToken)
    {
        if (version.ContentLength is not null) return true;
        await using Stream? content = await resourceRepository.GetResourceStreamAsync(ContainerName, version.Name, version.VersionId, cancellationToken);
        version.ContentLength = content?.Length;
        return content is not null;
    }

    public Task<bool> SetExpiryAsync(string resourceName, string versionId, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_expiry", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (string.IsNullOrEmpty(versionId))
                throw new ArgumentException($"{nameof(versionId)} cannot be empty.", nameof(versionId));
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken);
            return await resourceRepository.SetExpiryAsync(new ResourceLocation(ContainerName, resourceName), versionId, expiresAt, cancellationToken);
        });

    public Task<ResourceContainerUsage> GetUsageAsync(CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("get_usage", ContainerName, null, () => resourceRepository.GetContainerUsageAsync(ContainerName, cancellationToken));

    public Task<bool> ExistsAsync(string resourceName, string? versionId = null)
        => ExistsAsync(resourceName, versionId, CancellationToken.None);

    public Task<bool> ExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
        => _telemetry.RunAsync("exists", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            return await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken) != null;
        });

    // Container clients now share the service client's scope, so there is
    // nothing to dispose. IDisposable is retained for backwards compatibility.
    public void Dispose() => GC.SuppressFinalize(this);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pruning older versions of {ContainerName}/{ResourceName} failed after its upload was stored; they stay until a later upload of the name prunes them.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception, string containerName, string resourceName);
}
