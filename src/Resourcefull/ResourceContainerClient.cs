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
        ResourceNames.ThrowIfInvalidResourceName(resourceName);

        uploadOptions ??= new ResourceUploadOptions();

        // Without versioning, an upload that may not overwrite is a create-only write.
        bool createOnly = uploadOptions.CreateOnly || (!options.IsVersioningEnabled && !uploadOptions.Overwrite);

        var resourceInfo = new ResourceInfo
        {
            ContainerName = ContainerName,
            Name = resourceName,
            VersionId = Guid.CreateVersion7().ToString(),
            LastModified = DateTime.UtcNow,
            ContentType = uploadOptions.Headers?.ContentType,
            ContentLength = uploadOptions.Headers?.ContentLength,
            Metadata = uploadOptions.Metadata
        };

        try
        {
            return await SaveNewResourceVersionAsync(content, resourceName, resourceInfo, createOnly, cancellationToken);
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

        Stream? stream = await resourceRepository.GetResourceStreamAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (stream == null) return null;

        var newResourceInfo = new ResourceInfo
        {
            ContainerName = resourceInfo.ContainerName,
            Name = resourceInfo.Name,
            VersionId = Guid.CreateVersion7().ToString(),
            LastModified = DateTime.UtcNow,
            ContentType = resourceInfo.ContentType,
            ContentLength = resourceInfo.ContentLength,
            Metadata = resourceInfo.Metadata
        };

        return await SaveNewResourceVersionAsync(stream, resourceName, newResourceInfo, createOnly: false, cancellationToken);
    }

    private async Task<ResourceInfo> SaveNewResourceVersionAsync(Stream stream, string resourceName, ResourceInfo resourceInfo, bool createOnly, CancellationToken cancellationToken)
    {
        resourceInfo.Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(ContainerName, resourceName, resourceInfo.VersionId));

        if (stream.CanSeek) stream.Position = 0;
        await resourceRepository.AddResourceAsync(ContainerName, resourceInfo, stream, createOnly, cancellationToken);

        // Prune only once the new version is stored, keeping the newest versions in store order. Reading the versions
        // after the write means concurrent writers each see all of them, so together they never keep more than the
        // limit; without versioning the limit is one, which is what an overwrite leaves.
        int keep = options.IsVersioningEnabled ? options.MaxVersions : 1;
        List<ResourceInfo> versions = await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken);
        foreach (ResourceInfo version in versions.Skip(keep))
        {
            await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, version.VersionId, cancellationToken);
        }

        return resourceInfo;
    }

    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId = null)
        => DownloadStreamingAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);

        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(ContainerName, resourceName, versionId, cancellationToken);
        if (resourceInfo == null) return null;

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
        if (result?.Stream == null) return null;

        using var memoryStream = new MemoryStream();
        await result.Stream.CopyToAsync(memoryStream, cancellationToken);
        return new ResourceContent(result.ResourceInfo, BinaryData.FromBytes(memoryStream.ToArray()));
    }

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId = null)
        => GetResourceInfoAsync(resourceName, versionId, CancellationToken.None);

    public async Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
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
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        return await resourceRepository.GetResourceVersionsAsync(ContainerName, resourceName, cancellationToken);
    }

    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null)
        => DeleteResourceIfExistsAsync(resourceName, versionId, CancellationToken.None);

    public async Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        return await resourceRepository.DeleteResourceAsync(ContainerName, resourceName, versionId, cancellationToken);
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
}
