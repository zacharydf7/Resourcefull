using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull;

internal class ResourceServiceClient(IResourceRepository resourceRepository, IOptions<ResourcefullOptions> options, IUriProvider? uriProvider = null) : IResourceServiceClient
{
    private readonly ResourcefullOptions _options = options.Value;

    public IResourceContainerClient GetResourceContainerClient(string resourceContainerName)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        return new ResourceContainerClient(resourceRepository, uriProvider, _options, new ResourceContainerInfo { Name = resourceContainerName });
    }

    public Task<IResourceContainerClient> CreateResourceContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata = null)
        => CreateResourceContainerAsync(resourceContainerName, metadata, CancellationToken.None);

    public async Task<IResourceContainerClient> CreateResourceContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        return await CreateContainerCoreAsync(resourceContainerName, metadata, cancellationToken);
    }

    public Task<IResourceContainerClient> CreateResourceContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata = null)
        => CreateResourceContainerIfNotExistsAsync(resourceContainerName, metadata, CancellationToken.None);

    public async Task<IResourceContainerClient> CreateResourceContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);

        // The usual case is a container that already exists; finding it with a read avoids a failed insert each time.
        if (await ContainerExistsAsync(resourceContainerName, cancellationToken))
            return GetResourceContainerClient(resourceContainerName);

        try
        {
            return await CreateContainerCoreAsync(resourceContainerName, metadata, cancellationToken);
        }
        catch (ResourceConflictException)
        {
            // Already there, whether from before or from a concurrent create; either way the caller gets a client.
            return GetResourceContainerClient(resourceContainerName);
        }
    }

    private async Task<IResourceContainerClient> CreateContainerCoreAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        var containerInfo = new ResourceContainerInfo
        {
            Name = resourceContainerName,
            Metadata = metadata ?? new Dictionary<string, string>(),
            Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(resourceContainerName))
        };

        await resourceRepository.AddResourceContainerAsync(containerInfo, cancellationToken);
        return new ResourceContainerClient(resourceRepository, uriProvider, _options, containerInfo);
    }

    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName)
        => GetResourceContainerInfoAsync(resourceContainerName, CancellationToken.None);

    public async Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        return await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken);
    }

    public Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync()
        => GetResourceContainersAsync(CancellationToken.None);

    public async Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync(CancellationToken cancellationToken)
        => await resourceRepository.GetAllResourceContainerInfoAsync(cancellationToken);

    public Task<bool> ContainerExistsAsync(string resourceContainerName)
        => ContainerExistsAsync(resourceContainerName, CancellationToken.None);

    public async Task<bool> ContainerExistsAsync(string resourceContainerName, CancellationToken cancellationToken)
        => await GetResourceContainerInfoAsync(resourceContainerName, cancellationToken) != null;

    public Task<bool> DeleteResourceContainerIfExistsAsync(string resourceContainerName)
        => DeleteResourceContainerIfExistsAsync(resourceContainerName, CancellationToken.None);

    public async Task<bool> DeleteResourceContainerIfExistsAsync(string resourceContainerName, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);

        if (!await ContainerExistsAsync(resourceContainerName, cancellationToken))
        {
            return false;
        }
        return await resourceRepository.DeleteResourceContainerAsync(resourceContainerName, cancellationToken);
    }
}
