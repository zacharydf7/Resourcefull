using Resourcefull.InMemory.Models;
using System.Runtime.CompilerServices;

// Required for Moq
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace Resourcefull.InMemory;

internal class ResourceInMemoryRepository : IResourceRepository
{

    private readonly List<InMemoryResourceContainer> _resourceContainers = [];
    private readonly List<InMemoryResource> _resources = [];
    private long _lastSequence;

    public async Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResourceContainer correspondingContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName)
            ?? throw new ResourceContainerNotFoundException($"Resource Container with the name {resourceContainerName} does not exist.");

        if (createOnly && correspondingContainer.Resources.Any(r => r.Name == resourceInfo.Name))
            throw new ResourceConflictException($"Resource with name {resourceInfo.Name} already exists.");

        if (stream.CanSeek) stream.Position = 0;

        var memoryStream = new MemoryStream();
        using var content = new HashingStream(stream);
        await content.CopyToAsync(memoryStream, cancellationToken);
        WrittenContent written = content.Complete(resourceInfo.ContentLength);
        memoryStream.Position = 0;

        var newResource = new InMemoryResource()
        {
            ContainerName = resourceContainerName,
            Name = resourceInfo.Name,
            Stream = memoryStream,
            Sequence = ++_lastSequence,
            ResourceInfo = new ResourceInfo()
            {
                ContainerName = resourceContainerName,
                Name = resourceInfo.Name,
                VersionId = resourceInfo.VersionId,
                LastModified = resourceInfo.LastModified,
                ContentType = resourceInfo.ContentType,
                ContentLength = written.ContentLength,
                Metadata = new Dictionary<string, string>(resourceInfo.Metadata),
                Uri = resourceInfo.Uri
            }
        };
        _resources.Add(newResource);
        correspondingContainer.Resources.Add(newResource);

        return written;
    }

    public async Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResourceContainer? resourceContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName);

        if (resourceContainer != null)
        {
            var groups = resourceContainer.Resources
            .GroupBy(r => r.Name);

            return await Task.FromResult<List<ResourceInfo>>([.. groups.SelectMany(g => g.OrderByDescending(r => r.Sequence).Take(1)
            .Select(r => new ResourceInfo()
            {
                ContainerName = resourceContainerName,
                Name = r.Name,
                VersionId = r.ResourceInfo.VersionId,
                LastModified = r.ResourceInfo.LastModified,
                IsLatest = true,
                ContentType = r.ResourceInfo.ContentType,
                ContentLength = r.ResourceInfo.ContentLength,
                Metadata = new Dictionary<string, string>(r.ResourceInfo.Metadata),
                Uri = r.ResourceInfo.Uri
            }).ToList())]);
        }
        else
        {
            return await Task.FromResult(new List<ResourceInfo>());
        }
    }

    public async Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResource? resource = versionId == null
            ? _resources.OrderByDescending(r => r.Sequence).FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            : _resources.FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.ResourceInfo.VersionId == versionId);

        if (resource is null) return null;

        return await Task.FromResult(ToResourceInfo(resource, IsLatestVersion(resourceContainerName, resourceName, resource.ResourceInfo.VersionId)));
    }

    public async Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResource? resource = versionId == null
            ? _resources.OrderByDescending(r => r.Sequence).FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            : _resources.FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.ResourceInfo.VersionId == versionId);

        if (resource is null) return null;

        var memoryStream = new MemoryStream();
        await resource.Stream.CopyToAsync(memoryStream, cancellationToken);

        memoryStream.Position = 0;
        resource.Stream.Position = 0;

        return memoryStream;
    }

    public async Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Without a version, every version of the name goes.
        List<InMemoryResource> doomed = [.. _resources.Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName
            && (versionId == null || r.ResourceInfo.VersionId == versionId))];

        if (doomed.Count == 0) return false;

        InMemoryResourceContainer? correspondingContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName);
        foreach (InMemoryResource resource in doomed)
        {
            await resource.Stream.DisposeAsync();
            _resources.Remove(resource);
            correspondingContainer?.Resources.Remove(resource);
        }

        return true;
    }

    public async Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_resourceContainers.Any(x => x.Name == resourceContainerInfo.Name))
            throw new ResourceConflictException($"Resource Container with the name {resourceContainerInfo.Name} already exists.");

        _resourceContainers.Add(new InMemoryResourceContainer()
        {
            Name = resourceContainerInfo.Name
        });

        await Task.CompletedTask;
    }

    public async Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.FromResult(_resourceContainers.Select(x => new ResourceContainerInfo()
        {
            Name = x.Name
        }).ToList());
    }

    public async Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await Task.FromResult(_resourceContainers.Where(x => x.Name == resourceContainerName).Select(x => new ResourceContainerInfo()
        {
            Name = x.Name
        }).FirstOrDefault());
    }

    public async Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResourceContainer? existingResourceContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName);
        if (existingResourceContainer is null) return false;

        foreach (InMemoryResource resource in _resources.Where(r => r.ContainerName == resourceContainerName))
        {
            await resource.Stream.DisposeAsync();
        }

        _resources.RemoveAll(r => r.ContainerName == resourceContainerName);
        _resourceContainers.Remove(existingResourceContainer);

        return true;
    }

    public async Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResourceContainer? resourceContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName);
        if (resourceContainer != null)
        {
            var ordered = resourceContainer.Resources
                .Where(x => x.Name == resourceName)
                .OrderByDescending(x => x.Sequence)
                .ToList();

            return await Task.FromResult(ordered
                .Select((x, index) => new ResourceInfo()
                {
                    ContainerName = x.ContainerName,
                    Name = x.Name,
                    VersionId = x.ResourceInfo.VersionId,
                    LastModified = x.ResourceInfo.LastModified,
                    IsLatest = index == 0,
                    ContentType = x.ResourceInfo.ContentType,
                    ContentLength = x.ResourceInfo.ContentLength,
                    Metadata = new Dictionary<string, string>(x.ResourceInfo.Metadata),
                    Uri = x.ResourceInfo.Uri
                }).ToList());
        }
        else
        {
            return await Task.FromResult(new List<ResourceInfo>());
        }
    }

    private bool IsLatestVersion(string resourceContainerName, string resourceName, string? versionId)
    {
        InMemoryResource? latest = _resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            .OrderByDescending(r => r.Sequence)
            .FirstOrDefault();
        return latest is not null && latest.ResourceInfo.VersionId == versionId;
    }

    private static ResourceInfo ToResourceInfo(InMemoryResource resource, bool isLatest) => new()
    {
        ContainerName = resource.ResourceInfo.ContainerName,
        Name = resource.ResourceInfo.Name,
        VersionId = resource.ResourceInfo.VersionId,
        LastModified = resource.ResourceInfo.LastModified,
        IsLatest = isLatest,
        ContentType = resource.ResourceInfo.ContentType,
        ContentLength = resource.ResourceInfo.ContentLength,
        Metadata = new Dictionary<string, string>(resource.ResourceInfo.Metadata),
        Uri = resource.ResourceInfo.Uri
    };
}