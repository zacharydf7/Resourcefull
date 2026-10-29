using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resourcefull;

internal partial class ResourceContainerClient(IResourceRepository resourceRepository, IUriProvider? uriProvider,
    ResourcefullOptions options, ResourceContainerInfo containerInfo, ILogger? logger) : IResourceContainerClient
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public ResourceContainerInfo ResourceContainerInfo { get; } = containerInfo;

    private string ContainerName => ResourceContainerInfo.Name;

    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions = null)
        => UploadAsync(resourceName, content, uploadOptions, CancellationToken.None);

    public async Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken)
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

        var resourceInfo = new ResourceInfo
        {
            ContainerName = ContainerName,
            Name = resourceName,
            VersionId = Guid.CreateVersion7().ToString(),
            ContentType = uploadOptions.Headers?.ContentType,
            ContentLength = uploadOptions.Headers?.ContentLength,
            Metadata = new Dictionary<string, string>(uploadOptions.Metadata)
        };

        try
        {
            return await SaveNewResourceVersionAsync(content, resourceName, resourceInfo, createOnly, conditions, cancellationToken);
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

    public async Task<ResourceInfo?> RestoreVersionAsync(string resourceName, string versionId, CancellationToken cancellationToken)
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

        var newResourceInfo = new ResourceInfo
        {
            ContainerName = resourceInfo.ContainerName,
            Name = resourceInfo.Name,
            VersionId = Guid.CreateVersion7().ToString(),
            ContentType = resourceInfo.ContentType,
            // Measured like any upload rather than declared: lengths stored by earlier releases came from callers.
            ContentLength = null,
            Metadata = resourceInfo.Metadata
        };

        return await SaveNewResourceVersionAsync(stream, resourceName, newResourceInfo, createOnly: false, conditions: null, cancellationToken);
    }

    private async Task<ResourceInfo> SaveNewResourceVersionAsync(Stream stream, string resourceName, ResourceInfo resourceInfo, bool createOnly,
        ResourceConditions? conditions, CancellationToken cancellationToken)
    {
        // Stored too, for applications on 2.x, which read it from storage, while they share a database with this release
        // during an upgrade.
        WithUri(resourceInfo);

        // Only a conditional upload takes the newer overload, so a provider written for 3.0 handles every other upload.
        WrittenContent written = conditions is null
            ? await resourceRepository.AddResourceAsync(ContainerName, resourceInfo, stream, createOnly, cancellationToken)
            : await resourceRepository.AddResourceAsync(ContainerName, resourceInfo, stream,
                new ResourceWriteCondition { CreateOnly = createOnly, Conditions = conditions }, cancellationToken);

        // Report what was stored rather than what was asked for.
        resourceInfo.ContentLength = written.ContentLength;
        resourceInfo.ContentHash = written.ContentHash;
        resourceInfo.LastModified = written.LastModified;
        resourceInfo.IsLatest = true;

        // The new version is stored, so the upload has succeeded; pruning is cleanup that neither the caller's token
        // nor a failure may turn into a reported failure.
        try
        {
            await PruneAsync(resourceName, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // The older versions stay until a later overwriting or versioned upload of the name prunes them; every
            // prune works from the versions as they stand after its own write. Logged, as a prune that keeps failing
            // would otherwise let storage grow unnoticed.
            try
            {
                LogPruneFailed(_logger, exception, ContainerName, resourceName);
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
    private async Task PruneAsync(string resourceName, CancellationToken cancellationToken)
    {
        int keep = options.IsVersioningEnabled ? options.MaxVersions : 1;
        List<ResourceInfo> versions = await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken);
        foreach (ResourceInfo version in versions.Skip(keep))
        {
            await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, version.VersionId, cancellationToken);
        }
    }

    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId = null)
        => DownloadStreamingAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);

        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (resourceInfo == null) return null;
        // Before the stream opens, so a URI provider that throws can't leave it undisposed.
        WithUri(resourceInfo);

        // Read the version just resolved, not "current" again: an upload in between would pair this info with other content.
        Stream? stream = await resourceRepository.GetResourceStreamAsync(ContainerName, resourceName, resourceInfo.VersionId, cancellationToken);
        if (stream == null) return null;

        return new ResourceDownloadResult(resourceInfo, stream);
    }

    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId = null)
        => DownloadContentAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceDownloadResult? result = await DownloadStreamingAsync(resourceName, versionId, cancellationToken);
        if (result is null) return null;

        await using Stream stream = result.Stream;
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        return new ResourceContent(result.ResourceInfo, BinaryData.FromBytes(memoryStream.ToArray()));
    }

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId = null)
        => GetResourceInfoAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
        return resourceInfo is null ? null : WithUri(resourceInfo);
    }

    public Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync()
        => GetResourcesAsync(CancellationToken.None);

    public async Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync(CancellationToken cancellationToken)
        => WithUris(await resourceRepository.GetAllResourceInfoAsync(ContainerName, cancellationToken));

    public async Task<ResourcePage> GetResourcePageAsync(ResourceListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        return WithUris(await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken));
    }

    // URIs come from the URI provider whenever a version is returned, not from storage, so a changed provider or base URL
    // applies to versions stored before the change as well.
    private ResourceInfo WithUri(ResourceInfo resourceInfo)
    {
        resourceInfo.Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(ContainerName, resourceInfo.Name, resourceInfo.VersionId));
        return resourceInfo;
    }

    private List<ResourceInfo> WithUris(List<ResourceInfo> resources)
    {
        foreach (ResourceInfo resourceInfo in resources) WithUri(resourceInfo);
        return resources;
    }

    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null)
        => DeleteResourceIfExistsAsync(resourceName, versionId, CancellationToken.None);

    public async Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        return await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, versionId, cancellationToken);
    }

    public async Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, ResourceConditions? conditions,
        CancellationToken cancellationToken)
    {
        if (conditions is null or { IsEmpty: true })
            return await DeleteResourceIfExistsAsync(resourceName, versionId, cancellationToken);

        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        return await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, versionId, conditions, cancellationToken);
    }

    public Task<bool> ExistsAsync(string resourceName, string? versionId = null)
        => ExistsAsync(resourceName, versionId, CancellationToken.None);

    public async Task<bool> ExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        return await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken) != null;
    }

    // Container clients now share the service client's scope, so there is
    // nothing to dispose. IDisposable is retained for backwards compatibility.
    public void Dispose() => GC.SuppressFinalize(this);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pruning older versions of {ContainerName}/{ResourceName} failed after its upload was stored; they stay until a later upload of the name prunes them.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception, string containerName, string resourceName);
}
