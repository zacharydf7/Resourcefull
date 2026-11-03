using Resourcefull.InMemory.Models;
using Resourcefull.Providers;

namespace Resourcefull.InMemory;

internal class ResourceInMemoryRepository(TimeProvider? timeProvider = null) : ResourceRepository
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // One gate guards all state, so every read sees a consistent snapshot and every change is atomic. It is a
    // semaphore so that waiting for it can be canceled; uploads buffer their content first, so it is never held
    // across I/O.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, InMemoryResourceContainer> _containers = new(StringComparer.Ordinal);
    private long _lastSequence;

    public override Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
        ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) =>
        AddVersionAsync(version, content, condition ?? new ResourceWriteCondition(), null, cancellationToken);

    public override Task<WrittenContent> ImportVersionAsync(NewResourceVersion version, Stream content,
        CancellationToken cancellationToken = default) =>
        AddVersionAsync(version, content, new ResourceWriteCondition(), version.LastModified, cancellationToken);

    /// <summary>
    /// Stores a new version as the name's current one: an upload, stamped with the time it becomes current, or an
    /// import, stamped with <paramref name="lastModified"/>, the time it was first stored.
    /// </summary>
    private async Task<WrittenContent> AddVersionAsync(NewResourceVersion version, Stream stream,
        ResourceWriteCondition condition, DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        (string resourceContainerName, string resourceName) = version.Location;
        bool createOnly = condition.CreateOnly;
        ResourceConditions? conditions = condition.Conditions;

        // Checked again with the insert below; this only spares reading the content of an upload that will fail. A
        // missing container is left for the insert to report, rather than unmet conditions.
        if (conditions is not null)
        {
            await UnderLockAsync(() =>
            {
                if (_containers.ContainsKey(resourceContainerName))
                    ThrowIfNotMet(conditions, resourceContainerName, resourceName,
                        VersionsOf(resourceContainerName, resourceName).MaxBy(r => r.Sequence));
                return true;
            }, cancellationToken);
        }

        using var buffer = new MemoryStream();
        using var content = new HashingStream(stream);
        await content.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();

        return await UnderLockAsync(() =>
        {
            // Checked in the same step as the insert, so a container deleted while the content was read is reported
            // rather than written into.
            InMemoryResourceContainer container = _containers.GetValueOrDefault(resourceContainerName)
                ?? throw ContainerMissing(resourceContainerName);

            if (createOnly && container.Resources.ContainsKey(resourceName))
                throw ResourceExists(resourceContainerName, resourceName);

            container.Resources.TryGetValue(resourceName, out List<InMemoryResource>? versions);
            if (versions != null && versions.Any(v => v.ResourceInfo.VersionId == version.VersionId))
            {
                throw new ResourceConflictException($"The resource {resourceName} in the container " +
                    $"{resourceContainerName} already has a version {version.VersionId}.")
                {
                    ContainerName = resourceContainerName, ResourceName = resourceName
                };
            }
            if (conditions is not null)
                ThrowIfNotMet(conditions, resourceContainerName, resourceName, versions?.MaxBy(r => r.Sequence));

            // Stamped as the version becomes current, not before its content was read. A wrong declared length throws
            // here, before anything is stored.
            WrittenContent written = content.Complete(version.DeclaredLength, lastModified ?? _clock.GetUtcNow());
            AddVersion(container, version, bytes, written);
            return written;
        }, cancellationToken);
    }

    public override Task<WrittenContent?> CopyResourceAsync(ResourceLocation source, string? sourceVersionId,
        NewResourceVersion destination, ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (Resolve(VersionsOf(source.ContainerName, source.ResourceName), sourceVersionId) is not { } from) return null;

            (string containerName, string resourceName) = destination.Location;
            InMemoryResourceContainer container = _containers.GetValueOrDefault(containerName)
                ?? throw ContainerMissing(containerName);
            container.Resources.TryGetValue(resourceName, out List<InMemoryResource>? versions);
            if (condition?.CreateOnly == true && versions is not null)
                throw ResourceExists(containerName, resourceName);
            if (condition?.Conditions is { } conditions)
                ThrowIfNotMet(conditions, containerName, resourceName, versions?.MaxBy(r => r.Sequence));

            // Stored content is never changed, so the copy can share it.
            var written = new WrittenContent(from.Content.Length, from.ResourceInfo.ContentHash!, _clock.GetUtcNow());
            AddVersion(container, destination, from.Content, written);
            return (WrittenContent?)written;
        }, cancellationToken);

    public override Task<bool> MoveResourceAsync(ResourceLocation source, ResourceLocation destination,
        ResourceConditions? sourceConditions, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (_containers.GetValueOrDefault(source.ContainerName) is not { } from
                || !from.Resources.TryGetValue(source.ResourceName, out List<InMemoryResource>? versions))
            {
                return false;
            }

            if (sourceConditions is not null)
                ThrowIfNotMet(sourceConditions, source.ContainerName, source.ResourceName,
                    versions.MaxBy(r => r.Sequence));
            InMemoryResourceContainer to = _containers.GetValueOrDefault(destination.ContainerName)
                ?? throw ContainerMissing(destination.ContainerName);
            if (to.Resources.ContainsKey(destination.ResourceName))
                throw ResourceExists(destination.ContainerName, destination.ResourceName);

            from.Resources.Remove(source.ResourceName);
            foreach (InMemoryResource version in versions)
            {
                version.ResourceInfo = version.ResourceInfo with
                {
                    ContainerName = destination.ContainerName, Name = destination.ResourceName
                };
            }
            to.Resources[destination.ResourceName] = versions;
            return true;
        }, cancellationToken);

    /// <summary>Stores a new version as the name's current one; called under the lock.</summary>
    private void AddVersion(InMemoryResourceContainer container, NewResourceVersion version, byte[] content, WrittenContent written)
    {
        string resourceName = version.Location.ResourceName;
        if (!container.Resources.TryGetValue(resourceName, out List<InMemoryResource>? versions))
            container.Resources[resourceName] = versions = [];

        versions.Add(new InMemoryResource
        {
            Content = content,
            Sequence = ++_lastSequence,
            ResourceInfo = new ResourceInfo
            {
                ContainerName = container.Name,
                Name = resourceName,
                VersionId = version.VersionId,
                LastModified = written.LastModified,
                ContentType = version.ContentType,
                ContentLength = written.ContentLength,
                ContentHash = written.ContentHash,
                Metadata = new Dictionary<string, string>(version.Metadata),
                CopiedFrom = version.CopiedFrom,
                ExpiresAt = version.ExpiresAt
            }
        });
    }

    public override Task<IReadOnlyList<ResourceInfo>> GetExpiredAsync(DateTimeOffset now, int maxVersions,
        (DateTimeOffset ExpiresAt, string VersionId)? after = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() =>
        [
            .. _containers.Values
                .SelectMany(container => container.Resources.Values)
                .SelectMany(versions => versions
                    .Where(v => v.ResourceInfo.ExpiresAt <= now)
                    .Select(v => ToResourceInfo(v, isLatest: v == versions.MaxBy(r => r.Sequence))))
                .Where(v => after is not { } last || v.ExpiresAt > last.ExpiresAt
                    || (v.ExpiresAt == last.ExpiresAt && string.CompareOrdinal(v.VersionId, last.VersionId) > 0))
                .OrderBy(v => v.ExpiresAt)
                .ThenBy(v => v.VersionId, StringComparer.Ordinal)
                .Take(maxVersions)
        ], cancellationToken);

    public override Task<bool> DeleteExpiredVersionAsync(ResourceLocation resource, string versionId, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (_containers.GetValueOrDefault(resource.ContainerName) is not { } container
                || !container.Resources.TryGetValue(resource.ResourceName, out List<InMemoryResource>? versions)
                || Resolve(versions, versionId) is not { } version
                || !(version.ResourceInfo.ExpiresAt <= now))
            {
                return false;
            }

            versions.Remove(version);
            if (versions.Count == 0) container.Resources.Remove(resource.ResourceName);
            return true;
        }, cancellationToken);

    public override Task<bool> SetExpiryAsync(ResourceLocation resource, string? versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info with { ExpiresAt = expiresAt }, cancellationToken);

    public override Task<bool> SetResourceMetadataAsync(ResourceLocation resource, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info with { Metadata = new Dictionary<string, string>(metadata) },
            cancellationToken);

    public override Task<bool> SetContentTypeAsync(ResourceLocation resource, string? versionId, string? contentType,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info with { ContentType = contentType }, cancellationToken);

    /// <summary>Replaces one version's <see cref="ResourceInfo"/> with what <paramref name="change"/> returns for it.</summary>
    /// <returns><see langword="false"/> if the version doesn't exist.</returns>
    private Task<bool> ChangeAsync(ResourceLocation resource, string? versionId, Func<ResourceInfo, ResourceInfo> change,
        CancellationToken cancellationToken) =>
        UnderLockAsync(() =>
        {
            if (Resolve(VersionsOf(resource.ContainerName, resource.ResourceName), versionId) is not { } version)
                return false;
            version.ResourceInfo = change(version.ResourceInfo);
            return true;
        }, cancellationToken);

    public override Task<IReadOnlyList<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? [.. container.Resources.Values.Select(versions => ToResourceInfo(versions.MaxBy(r => r.Sequence)!, isLatest: true))]
            : [], cancellationToken);

    public override Task<IReadOnlyList<ResourceInfo>> GetResourcePageAsync(string resourceContainerName, string? prefix,
        string? afterName, int pageSize, CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? [.. container.Resources
                .Where(pair => (prefix is null || pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    && (afterName is null || string.CompareOrdinal(pair.Key, afterName) > 0))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(pageSize)
                .Select(pair => ToResourceInfo(pair.Value.MaxBy(r => r.Sequence)!, isLatest: true))]
            : [], cancellationToken);

    public override Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceLocation resource,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() => [.. VersionsOf(resource.ContainerName, resource.ResourceName)
            .OrderByDescending(r => r.Sequence)
            .Select((r, index) => ToResourceInfo(r, isLatest: index == 0))], cancellationToken);

    public override Task<ResourceInfo?> GetResourceInfoAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            List<InMemoryResource> versions = VersionsOf(resource.ContainerName, resource.ResourceName);
            InMemoryResource? found = Resolve(versions, versionId);
            return found is null ? null : ToResourceInfo(found, isLatest: found == versions.MaxBy(r => r.Sequence));
        }, cancellationToken);

    public override Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        GetResourceStreamAsync(resource, versionId, 0, null, cancellationToken);

    public override Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId, long offset,
        long? length, CancellationToken cancellationToken = default) =>
        UnderLockAsync<Stream?>(() => Resolve(VersionsOf(resource.ContainerName, resource.ResourceName), versionId) is { } found
            ? new InMemoryResourceStream(found.Content, offset)
            : null, cancellationToken);

    public override Task<bool> DeleteResourceAsync(ResourceLocation resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            (string resourceContainerName, string resourceName) = resource;
            if (conditions is not null)
            {
                ThrowIfNotMet(conditions, resourceContainerName, resourceName,
                    VersionsOf(resourceContainerName, resourceName).MaxBy(r => r.Sequence));
            }

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

    public override Task<bool> SetContainerMetadataAsync(string resourceContainerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (!_containers.TryGetValue(resourceContainerName, out InMemoryResourceContainer? container)) return false;
            container.Metadata.Clear();
            foreach (KeyValuePair<string, string> entry in metadata) container.Metadata[entry.Key] = entry.Value;
            return true;
        }, cancellationToken);

    public override Task AddContainerAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            var container = new InMemoryResourceContainer
            {
                Name = resourceContainerName,
                Metadata = new Dictionary<string, string>(metadata)
            };
            if (!_containers.TryAdd(resourceContainerName, container))
            {
                throw new ResourceConflictException($"The container {resourceContainerName} already exists.")
                {
                    ContainerName = resourceContainerName
                };
            }
            return true;
        }, cancellationToken);

    public override Task<IReadOnlyList<ContainerInfo>> GetAllContainerInfoAsync(
        CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ContainerInfo>>(() => [.. _containers.Values.Select(ToContainerInfo)],
            cancellationToken);

    public override Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? ToContainerInfo(container)
            : null, cancellationToken);

    public override Task<bool> DeleteContainerAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.Remove(resourceContainerName), cancellationToken);

    public override Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? new ResourceContainerUsage(container.Resources.Values.Sum(versions => versions.Sum(v => (long)v.Content.Length)),
                container.Resources.Count, container.Resources.Values.Sum(versions => (long)versions.Count))
            : new ResourceContainerUsage(0, 0, 0), cancellationToken);

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

    /// <summary>A name with no current version meets no condition; empty conditions are no conditions.</summary>
    private static ResourceContainerNotFoundException ContainerMissing(string resourceContainerName) =>
        new($"The container {resourceContainerName} doesn't exist.") { ContainerName = resourceContainerName };

    private static ResourceConflictException ResourceExists(string resourceContainerName, string resourceName) =>
        new($"The resource {resourceName} already exists in the container {resourceContainerName}.")
        {
            ContainerName = resourceContainerName, ResourceName = resourceName
        };

    private static void ThrowIfNotMet(ResourceConditions conditions, string resourceContainerName, string resourceName,
        InMemoryResource? current)
    {
        if (conditions.IfMatchVersionId is null && conditions.IfMatchContentHash is null) return;

        string? versionId = current?.ResourceInfo.VersionId, contentHash = current?.ResourceInfo.ContentHash;
        if (current is null
            || (conditions.IfMatchVersionId is { } id && id != versionId)
            || (conditions.IfMatchContentHash is { } hash && hash != contentHash))
        {
            throw new ResourcePreconditionFailedException(current is null
                ? $"The resource {resourceName} in the container {resourceContainerName} has no current version to " +
                    "meet the conditions."
                : $"The current version {versionId} of the resource {resourceName} in the container " +
                    $"{resourceContainerName} doesn't meet the conditions.", versionId, contentHash)
            {
                ContainerName = resourceContainerName, ResourceName = resourceName
            };
        }
    }

    private List<InMemoryResource> VersionsOf(string resourceContainerName, string resourceName) =>
        _containers.GetValueOrDefault(resourceContainerName)?.Resources.GetValueOrDefault(resourceName) ?? [];

    private static InMemoryResource? Resolve(List<InMemoryResource> versions, string? versionId) => versionId == null
        ? versions.MaxBy(r => r.Sequence)
        : versions.FirstOrDefault(r => r.ResourceInfo.VersionId == versionId);

    private static ContainerInfo ToContainerInfo(InMemoryResourceContainer container) => new()
    {
        Name = container.Name,
        Metadata = new Dictionary<string, string>(container.Metadata)
    };

    // A copy of the stored metadata, so a caller can't change what's stored through it.
    private static ResourceInfo ToResourceInfo(InMemoryResource resource, bool isLatest) => resource.ResourceInfo with
    {
        IsLatest = isLatest,
        Metadata = new Dictionary<string, string>(resource.ResourceInfo.Metadata)
    };
}
