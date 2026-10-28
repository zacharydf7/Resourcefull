using Resourcefull.InMemory.Models;
using System.Runtime.CompilerServices;

// Required for Moq
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace Resourcefull.InMemory;

internal class ResourceInMemoryRepository : IResourceRepository
{

    private readonly List<InMemoryResourceContainer> _resourceContainers = [];
    private readonly List<InMemoryResource> _resources = [];

    public async Task AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResourceContainer correspondingContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName)
            ?? throw new ResourceContainerNotFoundException($"Resource Container with the name {resourceContainerName} does not exist.");

        if (stream.CanSeek) stream.Position = 0;

        var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        var newResource = new InMemoryResource()
        {
            ContainerName = resourceContainerName,
            Name = resourceInfo.Name,
            Stream = memoryStream,
            ResourceInfo = new ResourceInfo()
            {
                ContainerName = resourceContainerName,
                Name = resourceInfo.Name,
                VersionId = resourceInfo.VersionId,
                LastModified = resourceInfo.LastModified,
                ContentType = resourceInfo.ContentType,
                ContentLength = resourceInfo.ContentLength,
                Metadata = new Dictionary<string, string>(resourceInfo.Metadata),
                Uri = resourceInfo.Uri
            }
        };
        _resources.Add(newResource);
        correspondingContainer.Resources.Add(newResource);
    }

    public async Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResourceContainer? resourceContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName);

        if (resourceContainer != null)
        {
            var groups = resourceContainer.Resources
            .GroupBy(r => r.Name);

            return await Task.FromResult<List<ResourceInfo>>([.. groups.SelectMany(g => g.OrderByDescending(r => r.ResourceInfo.LastModified).Take(1)
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
            ? _resources.OrderByDescending(r => r.ResourceInfo.LastModified).FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            : _resources.FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.ResourceInfo.VersionId == versionId);

        if (resource is null) return null;

        return await Task.FromResult(ToResourceInfo(resource, IsLatestVersion(resourceContainerName, resourceName, resource.ResourceInfo.VersionId)));
    }

    public async Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResource? resource = versionId == null
            ? _resources.OrderByDescending(r => r.ResourceInfo.LastModified).FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            : _resources.FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.ResourceInfo.VersionId == versionId);

        if (resource is null) return null;

        var memoryStream = new MemoryStream();
        await resource.Stream.CopyToAsync(memoryStream, cancellationToken);

        memoryStream.Position = 0;
        resource.Stream.Position = 0;

        return memoryStream;
    }

    public async Task<bool> UpdateResourceAsync(string resourceContainerName, string resourceName, ResourceInfo resourceInfo, Stream stream, string? versionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResource? existingResource = versionId == null
            ? _resources.OrderByDescending(r => r.ResourceInfo.LastModified).FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            : _resources.FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.ResourceInfo.VersionId == versionId);

        if (existingResource is null) return false;

        if (stream.CanSeek) stream.Position = 0;

        MemoryStream? memoryStream = null;
        if (stream.Position != stream.Length)
        {
            memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;
        }

        existingResource.ResourceInfo.Name = resourceInfo.Name;
        existingResource.ResourceInfo.VersionId = resourceInfo.VersionId;
        existingResource.ResourceInfo.LastModified = resourceInfo.LastModified;
        existingResource.ResourceInfo.ContentType = resourceInfo.ContentType;
        existingResource.ResourceInfo.ContentLength = resourceInfo.ContentLength;
        existingResource.ResourceInfo.Metadata = new Dictionary<string, string>(resourceInfo.Metadata);
        existingResource.ResourceInfo.Uri = resourceInfo.Uri;
        existingResource.Stream = memoryStream ?? existingResource.Stream;

        return await Task.FromResult(true);
    }

    public async Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        InMemoryResource? existingResource = versionId == null
            ? _resources.Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
                .OrderByDescending(r => r.ResourceInfo.LastModified).FirstOrDefault()
            : _resources.FirstOrDefault(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.ResourceInfo.VersionId == versionId);

        if (existingResource is null) return false;

        await existingResource.Stream.DisposeAsync();
        _resources.Remove(existingResource);
        InMemoryResourceContainer? correspondingContainer = _resourceContainers.FirstOrDefault(x => x.Name == resourceContainerName);
        correspondingContainer?.Resources.Remove(existingResource);

        return await Task.FromResult(true);
    }

    public async Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _resourceContainers.Add(new InMemoryResourceContainer()
        {
            Name = resourceContainerInfo.Name
        });
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
                .OrderByDescending(x => x.ResourceInfo.LastModified)
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
            .OrderByDescending(r => r.ResourceInfo.LastModified)
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