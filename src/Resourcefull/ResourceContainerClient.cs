namespace Resourcefull;

internal class ResourceContainerClient(IResourceRepository resourceRepository, IUriProvider? uriProvider,
    ResourcefullOptions options, ResourceContainerInfo containerInfo) : IResourceContainerClient
{
    public ResourceContainerInfo ResourceContainerInfo { get; } = containerInfo;

    private string ContainerName => ResourceContainerInfo.Name;

    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions = null)
        => UploadAsync(resourceName, content, uploadOptions, CancellationToken.None);

    public async Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(resourceName))
            throw new ArgumentException("Resource name cannot be null or empty.", nameof(resourceName));

        uploadOptions ??= new ResourceUploadOptions();

        ResourceInfo? existingResource = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, null, cancellationToken);
        if (!options.IsVersioningEnabled && existingResource != null && !uploadOptions.Overwrite)
        {
            throw new ResourceConflictException(
                $"Resource with name {resourceName} already exists. " +
                $"Set {nameof(uploadOptions.Overwrite)} to true in {nameof(ResourceUploadOptions)} to overwrite existing resources.");
        }

        var now = DateTime.UtcNow;
        var resourceInfo = new ResourceInfo
        {
            ContainerName = ContainerName,
            Name = resourceName,
            VersionId = now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            LastModified = now,
            ContentType = uploadOptions.Headers?.ContentType,
            ContentLength = uploadOptions.Headers?.ContentLength,
            Metadata = uploadOptions.Metadata
        };

        return await SaveNewResourceVersionAsync(content, resourceName, resourceInfo, uploadOptions.Overwrite, cancellationToken);
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
        if (string.IsNullOrEmpty(resourceName))
            throw new ArgumentException($"{nameof(resourceName)} cannot be empty.", nameof(resourceName));
        if (string.IsNullOrEmpty(versionId))
            throw new ArgumentException($"{nameof(versionId)} cannot be empty.", nameof(versionId));

        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (resourceInfo == null) return null;

        Stream? stream = await resourceRepository.GetResourceStreamAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (stream == null) return null;

        var now = DateTime.UtcNow;
        var newResourceInfo = new ResourceInfo
        {
            ContainerName = resourceInfo.ContainerName,
            Name = resourceInfo.Name,
            VersionId = now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            LastModified = now,
            ContentType = resourceInfo.ContentType,
            ContentLength = resourceInfo.ContentLength,
            Metadata = resourceInfo.Metadata
        };

        return await SaveNewResourceVersionAsync(stream, resourceName, newResourceInfo, overwrite: false, cancellationToken);
    }

    private async Task<ResourceInfo> SaveNewResourceVersionAsync(Stream stream, string resourceName, ResourceInfo resourceInfo, bool overwrite, CancellationToken cancellationToken)
    {
        List<ResourceInfo> priorVersions = await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken) ?? [];

        resourceInfo.Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(ContainerName, resourceName, resourceInfo.VersionId));

        if (stream.CanSeek) stream.Position = 0;
        await resourceRepository.AddResourceAsync(ContainerName, resourceInfo, stream, cancellationToken);

        // Prune only once the new version is stored.
        if (options.IsVersioningEnabled)
        {
            int excess = priorVersions.Count + 1 - options.MaxVersions;
            foreach (ResourceInfo version in priorVersions.TakeLast(Math.Max(excess, 0)))
            {
                await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, version.VersionId, cancellationToken);
            }
        }
        else if (overwrite)
        {
            foreach (ResourceInfo version in priorVersions)
            {
                await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, version.VersionId, cancellationToken);
            }
        }

        return resourceInfo;
    }

    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId = null)
        => DownloadStreamingAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(resourceName)) return null;

        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (resourceInfo == null) return null;

        Stream? stream = await resourceRepository.GetResourceStreamAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (stream == null) return null;

        return new ResourceDownloadResult(resourceInfo, stream);
    }

    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId = null)
        => DownloadContentAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceDownloadResult? result = await DownloadStreamingAsync(resourceName, versionId, cancellationToken);
        if (result?.Stream == null) return null;

        using var memoryStream = new MemoryStream();
        await result.Stream.CopyToAsync(memoryStream, cancellationToken);
        return new ResourceContent(result.ResourceInfo, BinaryData.FromBytes(memoryStream.ToArray()));
    }

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId = null)
        => GetResourceInfoAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(resourceName)) return null;
        return await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
    }

    public Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync()
        => GetResourcesAsync(CancellationToken.None);

    public async Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync(CancellationToken cancellationToken)
        => await resourceRepository.GetAllResourceInfoAsync(ContainerName, cancellationToken);

    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName)
        => GetResourceVersionsAsync(resourceName, CancellationToken.None);

    public async Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(resourceName)) return [];
        return await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken);
    }

    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null)
        => DeleteResourceIfExistsAsync(resourceName, versionId, CancellationToken.None);

    public async Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(resourceName)) return false;
        return await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, versionId, cancellationToken);
    }

    public Task<bool> ExistsAsync(string resourceName, string? versionId = null)
        => ExistsAsync(resourceName, versionId, CancellationToken.None);

    public async Task<bool> ExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(resourceName)) return false;
        return await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken) != null;
    }

    // Container clients now share the service client's scope, so there is
    // nothing to dispose. IDisposable is retained for backwards compatibility.
    public void Dispose() => GC.SuppressFinalize(this);
}
