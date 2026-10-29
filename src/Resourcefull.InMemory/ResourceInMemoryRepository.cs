using Resourcefull.InMemory.Models;

namespace Resourcefull.InMemory;

internal class ResourceInMemoryRepository(TimeProvider? timeProvider = null) : IResourceRepository
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // One gate guards all state, so every read sees a consistent snapshot and every change is atomic. It is a
    // semaphore so that waiting for it can be cancelled; uploads buffer their content first, so it is never held
    // across I/O.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, InMemoryResourceContainer> _containers = new(StringComparer.Ordinal);
    private long _lastSequence;

    public async Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default)
    {
        if (stream.CanSeek) stream.Position = 0;

        using var buffer = new MemoryStream();
        using var content = new HashingStream(stream);
        await content.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();

        return await UnderLockAsync(() =>
        {
            // Checked in the same step as the insert, so a container deleted while the content was read is reported
            // rather than written into.
            InMemoryResourceContainer container = _containers.GetValueOrDefault(resourceContainerName)
                ?? throw new ResourceContainerNotFoundException($"Resource Container with the name {resourceContainerName} does not exist.");

            if (createOnly && container.Resources.ContainsKey(resourceInfo.Name))
                throw new ResourceConflictException($"Resource with name {resourceInfo.Name} already exists.");

            container.Resources.TryGetValue(resourceInfo.Name, out List<InMemoryResource>? versions);
            if (versions != null && versions.Any(v => v.ResourceInfo.VersionId == resourceInfo.VersionId))
                throw new ResourceConflictException($"Resource {resourceInfo.Name} already has a version {resourceInfo.VersionId}.");

            // Stamped as the version becomes current, not before its content was read. A wrong declared length throws
            // here, before anything is stored.
            WrittenContent written = content.Complete(resourceInfo.ContentLength, _clock.GetUtcNow());
            if (versions == null)
                container.Resources[resourceInfo.Name] = versions = [];

            versions.Add(new InMemoryResource
            {
                Content = bytes,
                Sequence = ++_lastSequence,
                ResourceInfo = new ResourceInfo
                {
                    ContainerName = resourceContainerName,
                    Name = resourceInfo.Name,
                    VersionId = resourceInfo.VersionId,
                    LastModified = written.LastModified,
                    ContentType = resourceInfo.ContentType,
                    ContentLength = written.ContentLength,
                    ContentHash = written.ContentHash,
                    Metadata = new Dictionary<string, string>(resourceInfo.Metadata),
                    Uri = resourceInfo.Uri
                }
            });
            return written;
        }, cancellationToken);
    }

    public Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default) =>
        UnderLockAsync<List<ResourceInfo>>(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? [.. container.Resources.Values.Select(versions => ToResourceInfo(versions.MaxBy(r => r.Sequence)!, isLatest: true))]
            : [], cancellationToken);

    public Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default) =>
        UnderLockAsync<List<ResourceInfo>>(() => [.. VersionsOf(resourceContainerName, resourceName)
            .OrderByDescending(r => r.Sequence)
            .Select((r, index) => ToResourceInfo(r, isLatest: index == 0))], cancellationToken);

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            List<InMemoryResource> versions = VersionsOf(resourceContainerName, resourceName);
            InMemoryResource? resource = Resolve(versions, versionId);
            return resource is null ? null : ToResourceInfo(resource, isLatest: resource == versions.MaxBy(r => r.Sequence));
        }, cancellationToken);

    public Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync<Stream?>(() => Resolve(VersionsOf(resourceContainerName, resourceName), versionId) is { } resource
            ? new InMemoryResourceStream(resource.Content)
            : null, cancellationToken);

    public Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (_containers.GetValueOrDefault(resourceContainerName) is not { } container
                || !container.Resources.TryGetValue(resourceName, out List<InMemoryResource>? versions))
                return false;

            // Without a version, every version of the name goes.
            if (versionId == null)
                return container.Resources.Remove(resourceName);

            if (versions.RemoveAll(r => r.ResourceInfo.VersionId == versionId) == 0)
                return false;

            if (versions.Count == 0)
                container.Resources.Remove(resourceName);
            return true;
        }, cancellationToken);

    public Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            var container = new InMemoryResourceContainer
            {
                Name = resourceContainerInfo.Name,
                Uri = resourceContainerInfo.Uri,
                Metadata = new Dictionary<string, string>(resourceContainerInfo.Metadata)
            };
            if (!_containers.TryAdd(resourceContainerInfo.Name, container))
                throw new ResourceConflictException($"Resource Container with the name {resourceContainerInfo.Name} already exists.");
            return true;
        }, cancellationToken);

    public Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default) =>
        UnderLockAsync<List<ResourceContainerInfo>>(() => [.. _containers.Values.Select(ToContainerInfo)], cancellationToken);

    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? ToContainerInfo(container)
            : null, cancellationToken);

    public Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.Remove(resourceContainerName), cancellationToken);

    private async Task<T> UnderLockAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<InMemoryResource> VersionsOf(string resourceContainerName, string resourceName) =>
        _containers.GetValueOrDefault(resourceContainerName)?.Resources.GetValueOrDefault(resourceName) ?? [];

    private static InMemoryResource? Resolve(List<InMemoryResource> versions, string? versionId) => versionId == null
        ? versions.MaxBy(r => r.Sequence)
        : versions.FirstOrDefault(r => r.ResourceInfo.VersionId == versionId);

    private static ResourceContainerInfo ToContainerInfo(InMemoryResourceContainer container) => new()
    {
        Name = container.Name,
        Uri = container.Uri,
        Metadata = new Dictionary<string, string>(container.Metadata)
    };

    private static ResourceInfo ToResourceInfo(InMemoryResource resource, bool isLatest) => new()
    {
        ContainerName = resource.ResourceInfo.ContainerName,
        Name = resource.ResourceInfo.Name,
        VersionId = resource.ResourceInfo.VersionId,
        LastModified = resource.ResourceInfo.LastModified,
        IsLatest = isLatest,
        ContentType = resource.ResourceInfo.ContentType,
        ContentLength = resource.ResourceInfo.ContentLength,
        ContentHash = resource.ResourceInfo.ContentHash,
        Metadata = new Dictionary<string, string>(resource.ResourceInfo.Metadata),
        Uri = resource.ResourceInfo.Uri
    };
}
