using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Resourcefull.Providers;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull;

internal partial class ResourceServiceClient(ResourceRepository resourceRepository, IOptions<ResourcefullOptions> options, IUriProvider? uriProvider = null,
    ILogger<ResourceContainerClient>? containerLogger = null, ContainerPolicies? policies = null,
    IResourceCleanupHandler? cleanupHandler = null, TimeProvider? timeProvider = null, ExpirySettings? expiry = null,
    IResourceUploadInspector? uploadInspector = null) : IResourceServiceClient
{
    private readonly ResourcefullOptions _options = options.Value;
    private readonly ClientTelemetry _telemetry = new(Telemetry.ProviderName(resourceRepository), options.Value);

    public IResourceContainerClient GetContainerClient(string resourceContainerName)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
        return NewContainerClient(new ContainerInfo { Name = resourceContainerName });
    }

    public Task<IResourceContainerClient> CreateContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("create_container", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidNewContainerName(resourceContainerName);
            return await CreateContainerCoreAsync(resourceContainerName, metadata, cancellationToken).ConfigureAwait(false);
        });

    public Task<IResourceContainerClient> CreateContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("create_container", resourceContainerName, null,
            () => CreateIfNotExistsCoreAsync(resourceContainerName, metadata, cancellationToken));

    private async Task<IResourceContainerClient> CreateIfNotExistsCoreAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);

        // The usual case is a container that already exists; finding it with a read avoids a failed insert each time.
        if (await resourceRepository.GetContainerInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is { } existing)
            return NewContainerClient(existing);

        ResourceNames.ThrowIfInvalidNewContainerName(resourceContainerName);

        try
        {
            return await CreateContainerCoreAsync(resourceContainerName, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceConflictException)
        {
            // Created concurrently; the caller gets a client for it all the same, with what was stored (or, should it
            // already be gone again, what a client made without a read has).
            return await resourceRepository.GetContainerInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is { } stored
                ? NewContainerClient(stored)
                : GetContainerClient(resourceContainerName);
        }
    }

    private async Task<IResourceContainerClient> CreateContainerCoreAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        ContainerInfo containerInfo = WithUri(new ContainerInfo
        {
            Name = resourceContainerName,
            Metadata = metadata ?? new Dictionary<string, string>()
        });

        await resourceRepository.AddContainerAsync(resourceContainerName,
            new Dictionary<string, string>(containerInfo.Metadata), cancellationToken).ConfigureAwait(false);
        return new ResourceContainerClient(resourceRepository, uriProvider, _options, containerInfo, containerLogger, policies, cleanupHandler,
            timeProvider, expiry, uploadInspector);
    }

    private ResourceContainerClient NewContainerClient(ContainerInfo containerInfo)
        => new(resourceRepository, uriProvider, _options, WithUri(containerInfo), containerLogger, policies, cleanupHandler, timeProvider, expiry,
            uploadInspector);

    // URIs come from the URI provider whenever a container is returned, not from storage, so a changed provider or base
    // URL applies to containers created before the change as well.
    private ContainerInfo WithUri(ContainerInfo containerInfo)
    {
        containerInfo.Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(containerInfo.Name));
        return containerInfo;
    }

    public Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("get_container", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            ContainerInfo? containerInfo = await resourceRepository.GetContainerInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false);
            return containerInfo is null ? null : WithUri(containerInfo);
        });

    public Task<ContainerPage> GetContainerPageAsync(ContainerListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("list_containers", null, null, () => GetContainerPageCoreAsync(options, continuationToken, cancellationToken),
            "resourcefull.page_size", options?.PageSize ?? ContainerListOptions.DefaultPageSize);

    private async Task<ContainerPage> GetContainerPageCoreAsync(ContainerListOptions? options, string? continuationToken,
        CancellationToken cancellationToken)
    {
        options ??= new ContainerListOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PageSize, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PageSize, ContainerListOptions.MaxPageSize, nameof(options));
        string? prefix = string.IsNullOrEmpty(options.Prefix) ? null : options.Prefix;
        string? afterName = continuationToken is null ? null : ResourcePageToken.Decode(continuationToken, prefix, nameof(continuationToken));

        // One more than a page, to tell whether another page follows without returning an empty last page.
        IReadOnlyList<ContainerInfo> containers = await resourceRepository.GetContainerPageAsync(prefix, afterName,
            options.PageSize + 1, cancellationToken).ConfigureAwait(false);
        string? next = null;
        if (containers.Count > options.PageSize)
        {
            containers = [.. containers.Take(options.PageSize)];
            next = ResourcePageToken.Encode(prefix, containers[^1].Name);
        }
        foreach (ContainerInfo containerInfo in containers) WithUri(containerInfo);
        return new ContainerPage(containers, next);
    }

    public Task<bool> ContainerExistsAsync(string resourceContainerName, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("container_exists", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            return await resourceRepository.GetContainerInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) != null;
        });

    public Task<int> PruneContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("prune_container", resourceContainerName, null, async activity =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            return await NewContainerClient(new ContainerInfo { Name = resourceContainerName }).PruneAllAsync(activity, cancellationToken).ConfigureAwait(false);
        });

    public Task<int> IndexMetadataAsync(string resourceContainerName, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("index_metadata", resourceContainerName, null, async activity =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            int indexed = await resourceRepository.IndexMetadataAsync(resourceContainerName, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("resourcefull.indexed", indexed);
            _telemetry.CountIndexed(indexed);
            return indexed;
        });

    public Task<bool> SetContainerMetadataAsync(string resourceContainerName, IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_container_metadata", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            ArgumentNullException.ThrowIfNull(metadata);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(resourceContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            return await resourceRepository.SetContainerMetadataAsync(resourceContainerName,
                new Dictionary<string, string>(metadata), cancellationToken).ConfigureAwait(false);
        });

    public Task<IReadOnlyList<ResourceInfo>> GetExpiredVersionsAsync(int maxVersions = 100, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync<IReadOnlyList<ResourceInfo>>("list_expired", null, null, async () =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxVersions, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(maxVersions, ResourceContainerClient.MaxListedVersions);
            IReadOnlyList<ResourceInfo> expired = await resourceRepository.GetExpiredAsync((timeProvider ?? TimeProvider.System).GetUtcNow(),
                maxVersions, null, cancellationToken).ConfigureAwait(false);
            return [.. expired.Select(v => v with
            {
                Uri = uriProvider?.GetAbsoluteUri(new ResourcefullContext(v.ContainerName, v.Name, v.VersionId))
            })];
        });

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        // What uploads that stopped partway left behind, which nothing else deletes; a provider that leaves nothing
        // behind answers at once. The provider counts them, so a sweep that finds nothing expired still shows them. A
        // failure here mustn't hold up the expired versions, so it's reported after them.
        int abandoned = 0;
        Exception? abandonedFailure = null;
        try
        {
            abandoned = await resourceRepository.DeleteAbandonedUploadsAsync(now - AbandonedUploadAge, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            abandonedFailure = exception;
        }

        // Most sweeps find nothing expired, and record nothing. One that does is an operation, even when every version it
        // finds is in a read-only container, which keeps them.
        if ((await resourceRepository.GetExpiredAsync(now, 1, null, cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            if (abandonedFailure is not null) ExceptionDispatchInfo.Throw(abandonedFailure);
            return 0;
        }

        return await _telemetry.RunAsync("expire", null, null, async activity =>
        {
            int deleted = 0, failed = 0, batches = 0, fallbacks = 0;
            // The first few failures are enough to say why; an outage of the handler's dependency fails every version.
            var failures = new List<Exception>();
            void Fail(Exception exception)
            {
                failed++;
                _telemetry.CountFailure(Telemetry.ExpireFailures, exception);
                if (failures.Count < FailuresKept) failures.Add(exception);
            }

            // Whether a version is gone, by whatever deleted it: a batch that failed partway may have.
            async Task<bool> GoneAsync(ResourceInfo version) =>
                await resourceRepository.GetResourceInfoAsync(new ResourceLocation(version.ContainerName, version.Name),
                    version.VersionId, CancellationToken.None).ConfigureAwait(false) is null;

            static (ResourceLocation, string) Key(ResourceInfo version) =>
                (new ResourceLocation(version.ContainerName, version.Name), version.VersionId);

            // Deletes a batch of versions together, or, if that fails, each on its own, so one version that fails
            // doesn't hold up the rest. Returns the versions deleted.
            async Task<List<ResourceInfo>> DeleteBatchAsync(List<ResourceInfo> versions)
            {
                try
                {
                    HashSet<(ResourceLocation, string)> deletedTogether =
                        [.. await resourceRepository.DeleteExpiredVersionsAsync([.. versions.Select(Key)], now,
                            cancellationToken).ConfigureAwait(false)];
                    batches++;
                    return [.. versions.Where(v => deletedTogether.Contains(Key(v)))];
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    fallbacks++;
                    _telemetry.RecordException(activity, exception);
                    var deletedOneByOne = new List<ResourceInfo>();
                    foreach (ResourceInfo version in versions)
                    {
                        try
                        {
                            var location = new ResourceLocation(version.ContainerName, version.Name);
                            // One the failed batch deleted before it failed counts as deleted, so what waited
                            // behind it is cleaned up.
                            if (await resourceRepository.DeleteExpiredVersionAsync(location, version.VersionId, now,
                                cancellationToken).ConfigureAwait(false) || await GoneAsync(version).ConfigureAwait(false))
                                deletedOneByOne.Add(version);
                        }
                        catch (Exception oneFailure) when (oneFailure is not OperationCanceledException
                            || !cancellationToken.IsCancellationRequested)
                        {
                            Fail(oneFailure);
                        }
                    }
                    return deletedOneByOne;
                }
            }

            // Each batch starts after the last one ended, so the versions a sweep skips or fails on can't hold up the rest.
            (DateTimeOffset ExpiresAt, string VersionId)? after = null;
            while (true)
            {
                IReadOnlyList<ResourceInfo> expired = await resourceRepository.GetExpiredAsync(now, ExpiredPerBatch, after, cancellationToken).ConfigureAwait(false);
                // The versions the handler has been told about, each with the replaced versions waiting behind it.
                var ready = new List<(ResourceInfo Version, IReadOnlyList<ResourceInfo> Waiting)>();
                foreach (ResourceInfo version in expired)
                {
                    try
                    {
                        // A read-only container keeps its versions, expired or not, until it's writable again.
                        if (policies is not null && await policies.IsReadOnlyAsync(version.ContainerName, _telemetry, cancellationToken).ConfigureAwait(false))
                            continue;

                        IReadOnlyList<ResourceInfo> waiting = version.IsLatest
                            ? await NewContainerClient(new ContainerInfo { Name = version.ContainerName })
                                .WaitingBehindAsync(version.Name, version.VersionId, cancellationToken).ConfigureAwait(false)
                            : [];
                        // Left while a version it replaced is protected: deleting it would make that version current again.
                        if (ResourceContainerClient.Retained(waiting, now) is not null) continue;

                        // Told first, so a handler that fails leaves the version for the next sweep rather than deleted unreported.
                        if (cleanupHandler is not null)
                        {
                            await _telemetry.NotifyCleanupAsync(cleanupHandler, new ResourceCleanup(version, ResourceCleanupReason.Expired, now),
                                cancellationToken).ConfigureAwait(false);
                        }
                        ready.Add((version, waiting));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // One version that keeps failing, a handler's timeout included, mustn't hold up the rest; only
                        // canceling the sweep stops it.
                        Fail(exception);
                    }
                }

                if (ready.Count > 0)
                {
                    List<ResourceInfo> gone;
                    try
                    {
                        gone = await DeleteBatchAsync([.. ready.Select(r => r.Version)]).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Canceled partway, the batch may have deleted some; what waited behind those is still
                        // cleaned up, as each prune is, so replaced content doesn't come back.
                        foreach ((ResourceInfo version, IReadOnlyList<ResourceInfo> waiting) in
                            ready.Where(r => r.Waiting.Count > 0))
                        {
                            if (await GoneAsync(version).ConfigureAwait(false))
                            {
                                await NewContainerClient(new ContainerInfo { Name = version.ContainerName })
                                    .DeleteWaitingAsync(version.Name, waiting).ConfigureAwait(false);
                            }
                        }
                        throw;
                    }
                    foreach ((ResourceInfo version, IReadOnlyList<ResourceInfo> waiting) in
                        ready.Where(r => gone.Contains(r.Version)))
                    {
                        deleted++;
                        _telemetry.CountExpired();
                        await NewContainerClient(new ContainerInfo { Name = version.ContainerName })
                            .DeleteWaitingAsync(version.Name, waiting).ConfigureAwait(false);
                    }
                }

                if (expired.Count < ExpiredPerBatch) break;
                if (expired[^1].VersionId == after?.VersionId)
                    throw new InvalidOperationException($"{resourceRepository.GetType().Name} listed the same expired versions again: its GetExpiredAsync must list the versions after the one given.");
                after = (expired[^1].ExpiresAt ?? throw new InvalidOperationException($"{resourceRepository.GetType().Name} listed an expired version without an expiry: its GetExpiredAsync must list only versions with an ExpiresAt."),
                    expired[^1].VersionId);
            }

            activity?.SetTag("resourcefull.expired", deleted);
            if (abandoned > 0) activity?.SetTag("resourcefull.abandoned_uploads", abandoned);
            activity?.SetTag("resourcefull.batches", batches);
            if (fallbacks > 0) activity?.SetTag("resourcefull.batch_fallbacks", fallbacks);
            if (failed > 0)
            {
                string alsoAbandoned = abandonedFailure is null
                    ? ""
                    : ", then the one from deleting what stopped uploads left behind";
                throw new AggregateException(
                    $"{failed} expired versions weren't deleted, and stay for the next sweep; " +
                        $"{deleted} were. The first {failures.Count} failures follow{alsoAbandoned}.",
                    abandonedFailure is null ? failures : [.. failures, abandonedFailure]);
            }
            if (abandonedFailure is not null) ExceptionDispatchInfo.Throw(abandonedFailure);
            return deleted;
        }).ConfigureAwait(false);
    }

    public async Task<int> PurgeDeletedAsync(CancellationToken cancellationToken = default)
    {
        // Nothing can be in a bin the provider doesn't have, and expiry still runs.
        if (!resourceRepository.SupportsSoftDelete) return 0;
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        // Most sweeps find nothing due, and record nothing.
        if ((await resourceRepository.GetPurgeableAsync(now, 1, null, cancellationToken).ConfigureAwait(false)).Count == 0) return 0;

        return await _telemetry.RunAsync("purge", null, null, async activity =>
        {
            int purged = 0, failed = 0;
            var failures = new List<Exception>();
            (DateTimeOffset PurgeAt, string VersionId)? after = null;
            while (true)
            {
                IReadOnlyList<ResourceInfo> due = await resourceRepository.GetPurgeableAsync(now, ExpiredPerBatch, after, cancellationToken).ConfigureAwait(false);
                foreach (ResourceInfo version in due)
                {
                    try
                    {
                        // A read-only container keeps its bin, as it keeps its expired versions.
                        if (policies is not null && await policies.IsReadOnlyAsync(version.ContainerName, _telemetry, cancellationToken).ConfigureAwait(false))
                            continue;
                        // Told first, so a handler that fails leaves the version for the next sweep rather than purged unreported.
                        if (cleanupHandler is not null)
                        {
                            await _telemetry.NotifyCleanupAsync(cleanupHandler, new ResourceCleanup(version, ResourceCleanupReason.Purged, now),
                                cancellationToken).ConfigureAwait(false);
                        }
                        int count = await resourceRepository.PurgeDeletedAsync(new ResourceLocation(version.ContainerName, version.Name),
                            version.VersionId, now, cancellationToken).ConfigureAwait(false);
                        purged += count;
                        _telemetry.CountPurged(count);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        // One version that keeps failing mustn't hold up the rest; only canceling the sweep stops it.
                        failed++;
                        _telemetry.CountFailure(Telemetry.PurgeFailures, exception);
                        if (failures.Count < FailuresKept) failures.Add(exception);
                    }
                }

                if (due.Count < ExpiredPerBatch) break;
                if (due[^1].VersionId == after?.VersionId)
                    throw new InvalidOperationException($"{resourceRepository.GetType().Name} listed the same versions to purge again: its GetPurgeableAsync must list the versions after the one given.");
                after = (due[^1].PurgeAt ?? throw new InvalidOperationException($"{resourceRepository.GetType().Name} listed a version to purge without a purge time: its GetPurgeableAsync must list only versions with a PurgeAt."),
                    due[^1].VersionId);
            }

            activity?.SetTag("resourcefull.purged", purged);
            if (failed > 0)
            {
                throw new AggregateException($"{failed} versions due to leave the bin weren't purged, and stay for the next sweep; " +
                    $"{purged} were. The first {failures.Count} failures follow.", failures);
            }
            return purged;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// How long after an upload last stored anything the sweep takes it as abandoned: a provider that stages content,
    /// as SQL Server's does, marks an upload as running every 10 minutes while its content keeps arriving. A day, so an
    /// upload session can resume after a dropped connection or a night's pause.
    /// </summary>
    internal static readonly TimeSpan AbandonedUploadAge = TimeSpan.FromHours(24);

    /// <summary>
    /// How many expired versions, or versions due to leave the bin, one query lists, so a cleanup holds a bounded batch
    /// at a time.
    /// </summary>
    private const int ExpiredPerBatch = 100;

    /// <summary>
    /// How many failures a sweep or a container prune keeps to throw, to say why without holding one per failed version
    /// or resource.
    /// </summary>
    internal const int FailuresKept = 10;

    public Task<ResourceExportResult> ExportContainerAsync(string resourceContainerName, Stream destination,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("export", resourceContainerName, null, async activity =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            ArgumentNullException.ThrowIfNull(destination);
            ContainerInfo container = await resourceRepository.GetContainerInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceContainerNotFoundException($"The container {resourceContainerName} doesn't exist.")
                {
                    ContainerName = resourceContainerName
                };

            ResourceExportResult exported = await ContainerArchive.ExportAsync(resourceRepository, container,
                destination, activity, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("resourcefull.resources", exported.ResourceCount);
            activity?.SetTag("resourcefull.versions", exported.VersionCount);
            return exported;
        });

    public Task<ResourceImportResult> ImportContainerAsync(string resourceContainerName, Stream source,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("import", resourceContainerName, null, async activity =>
        {
            ResourceNames.ThrowIfInvalidNewContainerName(resourceContainerName);
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanSeek)
                throw new ArgumentException("An import reads the zip's directory at its end, so it needs a stream that can seek, such as a file.", nameof(source));

            ZipArchive zip = await ZipArchive.CreateAsync(source, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null,
                cancellationToken).ConfigureAwait(false);
            await using var zipDisposal = zip.ConfigureAwait(false);
            ContainerArchive.Manifest manifest = await ContainerArchive.ReadManifestAsync(zip, cancellationToken).ConfigureAwait(false);
            ContainerArchive.ThrowIfUnimportable(manifest, resourceRepository);
            // Without the mark of an import the exported container may have been in the middle of.
            return await ImportNewContainerAsync(resourceContainerName, WithoutImportMark(manifest.Metadata),
                ContainerArchive.VersionsOf(zip, manifest).ToAsyncEnumerable(), "the export", activity, cancellationToken).ConfigureAwait(false);
        });

    /// <summary>The container metadata that marks an import still filling its container.</summary>
    internal const string ImportMarkerKey = "resourcefull.import", ImportMarkerValue = "in-progress";

    /// <returns><see langword="false"/> if the provider doesn't change container metadata.</returns>
    private async Task<bool> TrySetMetadataAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resourceRepository.SetContainerMetadataAsync(resourceContainerName, metadata,
                cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public Task<bool> DeleteContainerIfExistsAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("delete_container", resourceContainerName, null, async () =>
        {
            ResourceNames.ThrowIfInvalidContainerName(resourceContainerName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(resourceContainerName, _telemetry, cancellationToken).ConfigureAwait(false);

            // Through the repository rather than ContainerExistsAsync, so the delete records one operation, not two.
            if (await resourceRepository.GetContainerInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is null)
            {
                return false;
            }
            return await resourceRepository.DeleteContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false);
        });
}
