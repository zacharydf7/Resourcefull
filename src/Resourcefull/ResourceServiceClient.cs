using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull;

internal class ResourceServiceClient(IResourceRepository resourceRepository, IOptions<ResourcefullOptions> options, IUriProvider? uriProvider = null,
    ILogger<ResourceContainerClient>? containerLogger = null, ContainerPolicies? policies = null,
    IResourceCleanupHandler? cleanupHandler = null, TimeProvider? timeProvider = null) : IResourceServiceClient
{
    private readonly ResourcefullOptions _options = options.Value;
    private readonly ClientTelemetry _telemetry = new(Telemetry.ProviderName(resourceRepository), options.Value);

    public IResourceContainerClient GetResourceContainerClient(string resourceContainerName)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        return NewContainerClient(new ResourceContainerInfo { Name = resourceContainerName });
    }

    public Task<IResourceContainerClient> CreateResourceContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata = null)
        => CreateResourceContainerAsync(resourceContainerName, metadata, CancellationToken.None);

    public Task<IResourceContainerClient> CreateResourceContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
        => _telemetry.RunAsync("create_container", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            return await CreateContainerCoreAsync(resourceContainerName, metadata, cancellationToken);
        });

    public Task<IResourceContainerClient> CreateResourceContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata = null)
        => CreateResourceContainerIfNotExistsAsync(resourceContainerName, metadata, CancellationToken.None);

    public Task<IResourceContainerClient> CreateResourceContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
        => _telemetry.RunAsync("create_container", resourceContainerName, null,
            () => CreateIfNotExistsCoreAsync(resourceContainerName, metadata, cancellationToken));

    private async Task<IResourceContainerClient> CreateIfNotExistsCoreAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
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
        return new ResourceContainerClient(resourceRepository, uriProvider, _options, containerInfo, containerLogger, policies, cleanupHandler, timeProvider);
    }

    private ResourceContainerClient NewContainerClient(ResourceContainerInfo containerInfo)
        => new(resourceRepository, uriProvider, _options, WithUri(containerInfo), containerLogger, policies, cleanupHandler, timeProvider);

    // URIs come from the URI provider whenever a container is returned, not from storage, so a changed provider or base
    // URL applies to containers created before the change as well.
    private ResourceContainerInfo WithUri(ResourceContainerInfo containerInfo)
    {
        containerInfo.Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(containerInfo.Name));
        return containerInfo;
    }

    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName)
        => GetResourceContainerInfoAsync(resourceContainerName, CancellationToken.None);

    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken)
        => _telemetry.RunAsync("get_container", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            ResourceContainerInfo? containerInfo = await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken);
            return containerInfo is null ? null : WithUri(containerInfo);
        });

    public Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync()
        => GetResourceContainersAsync(CancellationToken.None);

    public Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync(CancellationToken cancellationToken)
        => _telemetry.RunAsync<IReadOnlyList<ResourceContainerInfo>>("list_containers", null, null, async () =>
        {
            List<ResourceContainerInfo> containers = await resourceRepository.GetAllResourceContainerInfoAsync(cancellationToken);
            foreach (ResourceContainerInfo containerInfo in containers) WithUri(containerInfo);
            return containers;
        });

    public Task<bool> ContainerExistsAsync(string resourceContainerName)
        => ContainerExistsAsync(resourceContainerName, CancellationToken.None);

    public Task<bool> ContainerExistsAsync(string resourceContainerName, CancellationToken cancellationToken)
        => _telemetry.RunAsync("container_exists", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            return await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken) != null;
        });

    public Task<bool> DeleteResourceContainerIfExistsAsync(string resourceContainerName)
        => DeleteResourceContainerIfExistsAsync(resourceContainerName, CancellationToken.None);

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        // Most sweeps find nothing expired, and record nothing. One that does is an operation, even when every version it
        // finds is in a read-only container, which keeps them.
        if ((await resourceRepository.GetExpiredAsync(now, 1, null, cancellationToken)).Count == 0) return 0;

        return await _telemetry.RunAsync("expire", null, null, async activity =>
        {
            int deleted = 0, failed = 0;
            // The first few failures are enough to say why; an outage of the handler's dependency fails every version.
            var failures = new List<Exception>();
            // Each batch starts after the last one ended, so the versions a sweep skips or fails on can't hold up the rest.
            ResourceInfo? after = null;
            while (true)
            {
                List<ResourceInfo> expired = await resourceRepository.GetExpiredAsync(now, ExpiredPerBatch, after, cancellationToken);
                foreach (ResourceInfo version in expired)
                {
                    try
                    {
                        // A read-only container keeps its versions, expired or not, until it's writable again.
                        if (policies is not null && await policies.IsReadOnlyAsync(version.ContainerName, _telemetry, cancellationToken))
                            continue;

                        // Told first, so a handler that fails leaves the version for the next sweep rather than deleted unreported.
                        if (cleanupHandler is not null)
                            await cleanupHandler.OnCleanupAsync(new ResourceCleanup(version, ResourceCleanupReason.Expired, now), cancellationToken);
                        if (await resourceRepository.DeleteExpiredVersionAsync(new ResourceLocation(version.ContainerName, version.Name),
                            version.VersionId!, now, cancellationToken))
                        {
                            deleted++;
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // One version that keeps failing, a handler's timeout included, mustn't hold up the rest; only
                        // canceling the sweep stops it.
                        failed++;
                        if (failures.Count < FailuresKept) failures.Add(exception);
                    }
                }

                if (expired.Count < ExpiredPerBatch) break;
                if (expired[^1].VersionId == after?.VersionId)
                    throw new InvalidOperationException($"{resourceRepository.GetType().Name} listed the same expired versions again: its GetExpiredAsync must list the versions after the one given.");
                after = expired[^1];
            }

            activity?.SetTag("resourcefull.expired", deleted);
            if (failed > 0)
                throw new AggregateException($"{failed} expired versions weren't deleted, and stay for the next sweep; {deleted} were. The first {failures.Count} failures follow.", failures);
            return deleted;
        });
    }

    /// <summary>How many expired versions one query lists, so a cleanup holds a bounded batch at a time.</summary>
    private const int ExpiredPerBatch = 100;

    /// <summary>How many of a sweep's failures it keeps to throw, to say why without holding one per failed version.</summary>
    private const int FailuresKept = 10;

    public Task<bool> DeleteResourceContainerIfExistsAsync(string resourceContainerName, CancellationToken cancellationToken)
        => _telemetry.RunAsync("delete_container", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(resourceContainerName, _telemetry, cancellationToken);

            // Through the repository rather than ContainerExistsAsync, so the delete records one operation, not two.
            if (await resourceRepository.GetResourceContainerInfoAsync(resourceContainerName, cancellationToken) is null)
            {
                return false;
            }
            return await resourceRepository.DeleteResourceContainerAsync(resourceContainerName, cancellationToken);
        });
}
