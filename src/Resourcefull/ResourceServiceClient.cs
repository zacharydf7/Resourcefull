using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull;

internal class ResourceServiceClient(IResourceRepository resourceRepository, IOptions<ResourcefullOptions> options, IUriProvider? uriProvider = null,
    ILogger<ResourceContainerClient>? containerLogger = null) : IResourceServiceClient
{
    private readonly ResourcefullOptions _options = options.Value;

    public IResourceContainerClient GetResourceContainerClient(string resourceContainerName)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        return NewContainerClient(new ResourceContainerInfo { Name = resourceContainerName });
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
        if (await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken) is { } existing)
            return NewContainerClient(existing);

        try
        {
            return await CreateContainerCoreAsync(resourceContainerName, metadata, cancellationToken);
        }
        catch (ResourceConflictException)
        {
            // Created concurrently; the caller gets a client for it all the same, with what was stored (or, should it
            // already be gone again, what a client made without a read has).
            return await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken) is { } stored
                ? NewContainerClient(stored)
                : GetResourceContainerClient(resourceContainerName);
        }
    }

    private async Task<IResourceContainerClient> CreateContainerCoreAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        // The URI is stored too, for applications on 2.x, which read it from storage, while they share a database with
        // this release during an upgrade.
        ResourceContainerInfo containerInfo = WithUri(new ResourceContainerInfo
        {
            Name = resourceContainerName,
            Metadata = metadata ?? new Dictionary<string, string>()
        });

        await resourceRepository.AddResourceContainerAsync(containerInfo, cancellationToken);
        return new ResourceContainerClient(resourceRepository, uriProvider, _options, containerInfo, containerLogger);
    }

    private ResourceContainerClient NewContainerClient(ResourceContainerInfo containerInfo)
        => new(resourceRepository, uriProvider, _options, WithUri(containerInfo), containerLogger);

    // URIs come from the URI provider whenever a container is returned, not from storage, so a changed provider or base
    // URL applies to containers created before the change as well.
    private ResourceContainerInfo WithUri(ResourceContainerInfo containerInfo)
    {
        containerInfo.Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(containerInfo.Name));
        return containerInfo;
    }

    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName)
        => GetResourceContainerInfoAsync(resourceContainerName, CancellationToken.None);

    public async Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        ResourceContainerInfo? containerInfo = await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken);
        return containerInfo is null ? null : WithUri(containerInfo);
    }

    public Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync()
        => GetResourceContainersAsync(CancellationToken.None);

    public async Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync(CancellationToken cancellationToken)
    {
        List<ResourceContainerInfo> containers = await resourceRepository.GetAllResourceContainerInfoAsync(cancellationToken);
        foreach (ResourceContainerInfo containerInfo in containers) WithUri(containerInfo);
        return containers;
    }

    public Task<bool> ContainerExistsAsync(string resourceContainerName)
        => ContainerExistsAsync(resourceContainerName, CancellationToken.None);

    public async Task<bool> ContainerExistsAsync(string resourceContainerName, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        return await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken) != null;
    }

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
