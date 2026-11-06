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
        (string resourceContainerName, string resourceName) = version.Resource;
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
            }, cancellationToken).ConfigureAwait(false);
        }

        using var buffer = new MemoryStream();
        using var content = new HashingStream(stream);
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
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
            // A version ID in the bin stays taken, so an undelete never meets the same ID.
            if ((versions ?? []).Concat(container.Deleted.GetValueOrDefault(resourceName) ?? [])
                .Any(v => v.ResourceInfo.VersionId == version.VersionId))
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
        }, cancellationToken).ConfigureAwait(false);
    }

    public override Task<WrittenContent?> CopyResourceAsync(ResourceAddress source, string? sourceVersionId,
        NewResourceVersion destination, ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (Resolve(VersionsOf(source.ContainerName, source.ResourceName), sourceVersionId) is not { } from) return null;

            (string containerName, string resourceName) = destination.Resource;
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

    public override Task<bool> MoveResourceAsync(ResourceAddress source, ResourceAddress destination,
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
        string resourceName = version.Resource.ResourceName;
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
                ExpiresAt = version.ExpiresAt,
                RetainUntil = version.RetainUntil,
                LegalHold = version.LegalHold
            }
        });
    }

    public override bool SupportsExpiry => true;

    public override Task<IReadOnlyList<ResourceInfo>> GetExpiredAsync(DateTimeOffset now, int maxVersions,
        (DateTimeOffset ExpiresAt, string VersionId)? after = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() =>
        [
            .. _containers.Values
                .SelectMany(container => container.Resources.Values)
                .SelectMany(versions => versions
                    .Where(v => v.ResourceInfo.ExpiresAt <= now && v.ResourceInfo.IsDeletableAt(now))
                    .Select(v => ToResourceInfo(v, isLatest: v == versions.MaxBy(r => r.Sequence))))
                .Where(v => after is not { } last || v.ExpiresAt > last.ExpiresAt
                    || (v.ExpiresAt == last.ExpiresAt && string.CompareOrdinal(v.VersionId, last.VersionId) > 0))
                .OrderBy(v => v.ExpiresAt)
                .ThenBy(v => v.VersionId, StringComparer.Ordinal)
                .Take(maxVersions)
        ], cancellationToken);

    public override Task<bool> DeleteExpiredVersionAsync(ResourceAddress resource, string versionId, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (_containers.GetValueOrDefault(resource.ContainerName) is not { } container
                || !container.Resources.TryGetValue(resource.ResourceName, out List<InMemoryResource>? versions)
                || Resolve(versions, versionId) is not { } version
                || !(version.ResourceInfo.ExpiresAt <= now)
                || !version.ResourceInfo.IsDeletableAt(now))
            {
                return false;
            }

            versions.Remove(version);
            if (versions.Count == 0) container.Resources.Remove(resource.ResourceName);
            return true;
        }, cancellationToken);

    public override Task<bool> SetExpiryAsync(ResourceAddress resource, string? versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info with { ExpiresAt = expiresAt }, cancellationToken);

    public override bool SupportsRetention => true;

    public override Task<bool> SetRetentionAsync(ResourceAddress resource, string? versionId, DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info.RetainUntil > retainUntil
            ? throw RetentionShortened(resource, info, retainUntil)
            : info with { RetainUntil = retainUntil }, cancellationToken);

    public override Task<bool> SetLegalHoldAsync(ResourceAddress resource, string? versionId, bool hold,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info with { LegalHold = hold }, cancellationToken);

    public override Task<bool> SetResourceMetadataAsync(ResourceAddress resource, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info with { Metadata = new Dictionary<string, string>(metadata) },
            cancellationToken);

    public override Task<bool> SetContentTypeAsync(ResourceAddress resource, string? versionId, string? contentType,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(resource, versionId, info => info with { ContentType = contentType }, cancellationToken);

    /// <summary>Replaces one version's <see cref="ResourceInfo"/> with what <paramref name="change"/> returns for it.</summary>
    /// <returns><see langword="false"/> if the version doesn't exist.</returns>
    private Task<bool> ChangeAsync(ResourceAddress resource, string? versionId, Func<ResourceInfo, ResourceInfo> change,
        CancellationToken cancellationToken) =>
        UnderLockAsync(() =>
        {
            if (Resolve(VersionsOf(resource.ContainerName, resource.ResourceName), versionId) is not { } version)
                return false;
            version.ResourceInfo = change(version.ResourceInfo);
            return true;
        }, cancellationToken);

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

    public override Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceAddress resource,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() => [.. VersionsOf(resource.ContainerName, resource.ResourceName)
            .OrderByDescending(r => r.Sequence)
            .Select((r, index) => ToResourceInfo(r, isLatest: index == 0))], cancellationToken);

    public override Task<ResourceInfo?> GetResourceInfoAsync(ResourceAddress resource, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            List<InMemoryResource> versions = VersionsOf(resource.ContainerName, resource.ResourceName);
            InMemoryResource? found = Resolve(versions, versionId);
            return found is null ? null : ToResourceInfo(found, isLatest: found == versions.MaxBy(r => r.Sequence));
        }, cancellationToken);

    public override Task<Stream?> GetResourceStreamAsync(ResourceAddress resource, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        GetResourceStreamAsync(resource, versionId, 0, null, cancellationToken);

    public override Task<Stream?> GetResourceStreamAsync(ResourceAddress resource, string? versionId, long offset,
        long? length, CancellationToken cancellationToken = default) =>
        UnderLockAsync<Stream?>(() => Resolve(VersionsOf(resource.ContainerName, resource.ResourceName), versionId) is { } found
            ? new InMemoryResourceStream(found.Content, offset)
            : null, cancellationToken);

    public override Task<bool> DeleteResourceAsync(ResourceAddress resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default) =>
        DeleteAsync(resource, versionId, conditions, null, cancellationToken);

    public override bool SupportsSoftDelete => true;

    public override Task<bool> SoftDeleteResourceAsync(ResourceAddress resource, string? versionId, ResourceConditions? conditions,
        DateTimeOffset purgeAt, CancellationToken cancellationToken = default) =>
        DeleteAsync(resource, versionId, conditions, purgeAt, cancellationToken);

    /// <summary>Deletes into the bin, until <paramref name="purgeAt"/>, or for good when that's <see langword="null"/>.</summary>
    private Task<bool> DeleteAsync(ResourceAddress resource, string? versionId, ResourceConditions? conditions, DateTimeOffset? purgeAt,
        CancellationToken cancellationToken) =>
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
            List<InMemoryResource> removed = [.. versions.Where(r => versionId is null || r.ResourceInfo.VersionId == versionId)];
            if (removed.Count == 0) return false;
            ThrowIfAnyRetained(removed);

            versions.RemoveAll(removed.Contains);
            if (versions.Count == 0) container.Resources.Remove(resourceName);
            if (purgeAt is { } at)
            {
                DateTimeOffset now = _clock.GetUtcNow();
                foreach (InMemoryResource version in removed)
                    version.ResourceInfo = version.ResourceInfo with { DeletedAt = now, PurgeAt = at };
                if (!container.Deleted.TryGetValue(resourceName, out List<InMemoryResource>? bin)) container.Deleted[resourceName] = bin = [];
                bin.AddRange(removed);
            }
            return true;
        }, cancellationToken);

    public override Task<DeletedVersionPage> GetDeletedPageAsync(string resourceContainerName, string? prefix,
        (DateTimeOffset DeletedAt, string VersionId)? after, int pageSize, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            List<ResourceInfo> found = [.. InBinOrder((_containers.GetValueOrDefault(resourceContainerName)?.Deleted ?? [])
                    .Where(pair => prefix is null || pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .SelectMany(pair => pair.Value))
                .Where(v => after is not { } last || v.DeletedAt < last.DeletedAt
                    || (v.DeletedAt == last.DeletedAt && string.CompareOrdinal(v.VersionId, last.VersionId) > 0))
                .Take(pageSize + 1)];
            if (found.Count <= pageSize) return new DeletedVersionPage(found, null);
            found.RemoveAt(pageSize);
            return new DeletedVersionPage(found, (found[^1].DeletedAt!.Value, found[^1].VersionId));
        }, cancellationToken);

    public override Task<IReadOnlyList<ResourceInfo>> GetDeletedVersionsAsync(ResourceAddress resource,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() =>
            [.. InBinOrder(_containers.GetValueOrDefault(resource.ContainerName)?.Deleted.GetValueOrDefault(resource.ResourceName) ?? [])],
            cancellationToken);

    /// <summary>Versions in the bin as it lists them: newest deletion first, then by version ID.</summary>
    private static IEnumerable<ResourceInfo> InBinOrder(IEnumerable<InMemoryResource> versions) => versions
        .Select(v => ToResourceInfo(v, isLatest: false))
        .OrderByDescending(v => v.DeletedAt)
        .ThenBy(v => v.VersionId, StringComparer.Ordinal);

    public override Task<int> UndeleteAsync(ResourceAddress resource, string? versionId, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (_containers.GetValueOrDefault(resource.ContainerName) is not { } container
                || !container.Deleted.TryGetValue(resource.ResourceName, out List<InMemoryResource>? bin))
                return 0;
            List<InMemoryResource> returning = [.. bin.Where(v => versionId is null || v.ResourceInfo.VersionId == versionId)];
            if (returning.Count == 0) return 0;

            bin.RemoveAll(returning.Contains);
            if (bin.Count == 0) container.Deleted.Remove(resource.ResourceName);
            foreach (InMemoryResource version in returning)
                version.ResourceInfo = version.ResourceInfo with { DeletedAt = null, PurgeAt = null };
            if (container.Resources.TryGetValue(resource.ResourceName, out List<InMemoryResource>? versions))
            {
                // They come back as older versions: the current one stays current.
                InMemoryResource current = versions.MaxBy(r => r.Sequence)!;
                if (returning.Max(r => r.Sequence) > current.Sequence) current.Sequence = ++_lastSequence;
                versions.AddRange(returning);
            }
            else
            {
                container.Resources[resource.ResourceName] = returning;
            }
            return returning.Count;
        }, cancellationToken);

    public override Task<int> PurgeDeletedAsync(ResourceAddress resource, string? versionId, DateTimeOffset? dueBy,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (_containers.GetValueOrDefault(resource.ContainerName) is not { } container
                || !container.Deleted.TryGetValue(resource.ResourceName, out List<InMemoryResource>? bin))
                return 0;
            int purged = bin.RemoveAll(v => (versionId is null || v.ResourceInfo.VersionId == versionId)
                && !(v.ResourceInfo.PurgeAt > dueBy));
            if (bin.Count == 0) container.Deleted.Remove(resource.ResourceName);
            return purged;
        }, cancellationToken);

    public override Task<IReadOnlyList<ResourceInfo>> GetPurgeableAsync(DateTimeOffset now, int maxVersions,
        (DateTimeOffset PurgeAt, string VersionId)? after = null, CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ResourceInfo>>(() =>
        [
            .. _containers.Values
                .SelectMany(container => container.Deleted.Values.SelectMany(bin => bin))
                .Select(v => ToResourceInfo(v, isLatest: false))
                .Where(v => v.PurgeAt <= now)
                .Where(v => after is not { } last || v.PurgeAt > last.PurgeAt
                    || (v.PurgeAt == last.PurgeAt && string.CompareOrdinal(v.VersionId, last.VersionId) > 0))
                .OrderBy(v => v.PurgeAt)
                .ThenBy(v => v.VersionId, StringComparer.Ordinal)
                .Take(maxVersions)
        ], cancellationToken);

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

    public override Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName, int pageSize,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync<IReadOnlyList<ContainerInfo>>(() => [.. _containers
            .Where(pair => (prefix is null || pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                && (afterName is null || string.CompareOrdinal(pair.Key, afterName) > 0))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(pageSize)
            .Select(pair => ToContainerInfo(pair.Value))], cancellationToken);

    public override Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? ToContainerInfo(container)
            : null, cancellationToken);

    public override Task StartStagedVersionAsync(NewResourceVersion version, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            (string resourceContainerName, string resourceName) = version.Resource;
            InMemoryResourceContainer container = _containers.GetValueOrDefault(resourceContainerName)
                ?? throw ContainerMissing(resourceContainerName);
            container.Staged.Add((resourceName, version.VersionId),
                new InMemoryStagedVersion { Version = version, StagedAt = _clock.GetUtcNow() });
            return true;
        }, cancellationToken);

    public override Task<StagedVersion?> GetStagedVersionAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => StagedOf(resource, versionId) is { } staged
            ? new StagedVersion(staged.Version, staged.Content.Length)
            : null, cancellationToken);

    public override async Task<long?> AppendStagedContentAsync(ResourceAddress resource, string versionId, long offset,
        Stream content, CancellationToken cancellationToken = default)
    {
        // Checked before the content is read, to spare reading an append that will fail, and again as it's added.
        if (await UnderLockAsync(() => ThrowIfNotAt(resource, versionId, offset), cancellationToken).ConfigureAwait(false) is null)
            return null;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await UnderLockAsync(() =>
        {
            if (ThrowIfNotAt(resource, versionId, offset) is not { } staged) return (long?)null;
            buffer.Position = 0;
            buffer.CopyTo(staged.Content);
            staged.StagedAt = _clock.GetUtcNow();
            return staged.Content.Length;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override Task<Stream?> GetStagedStreamAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => StagedOf(resource, versionId) is { } staged
            ? (Stream?)new MemoryStream(staged.Content.ToArray(), writable: false)
            : null, cancellationToken);

    public override Task<WrittenContent?> CompleteStagedVersionAsync(ResourceAddress resource, string versionId,
        long contentLength, string contentHash, ResourceWriteCondition? condition = null,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            (string resourceContainerName, string resourceName) = resource;
            if (ThrowIfNotAt(resource, versionId, contentLength) is not { } staged) return null;
            InMemoryResourceContainer container = _containers[resourceContainerName];
            container.Resources.TryGetValue(resourceName, out List<InMemoryResource>? versions);
            if (condition?.CreateOnly == true && versions is not null)
                throw ResourceExists(resourceContainerName, resourceName);
            if (condition?.Conditions is { } conditions)
                ThrowIfNotMet(conditions, resourceContainerName, resourceName, versions?.MaxBy(r => r.Sequence));

            var written = new WrittenContent(contentLength, contentHash, _clock.GetUtcNow());
            AddVersion(container, staged.Version, staged.Content.ToArray(), written);
            container.Staged.Remove((resourceName, versionId));
            return (WrittenContent?)written;
        }, cancellationToken);

    public override Task<bool> DeleteStagedVersionAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.GetValueOrDefault(resource.ContainerName) is { } container
            && container.Staged.Remove((resource.ResourceName, versionId)), cancellationToken);

    public override Task<int> DeleteAbandonedUploadsAsync(DateTimeOffset staleBefore, CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            int deleted = 0;
            foreach (InMemoryResourceContainer container in _containers.Values)
            {
                foreach (var key in container.Staged.Where(s => s.Value.StagedAt < staleBefore).Select(s => s.Key).ToList())
                {
                    container.Staged.Remove(key);
                    deleted++;
                }
            }
            return deleted;
        }, cancellationToken);

    /// <summary>The staged version, or <see langword="null"/> if there's none; called under the lock.</summary>
    private InMemoryStagedVersion? StagedOf(ResourceAddress resource, string versionId) =>
        _containers.GetValueOrDefault(resource.ContainerName)?.Staged.GetValueOrDefault((resource.ResourceName, versionId));

    /// <summary>
    /// The staged version, provided its content ends at <paramref name="offset"/>, or <see langword="null"/> if
    /// there's none; called under the lock.
    /// </summary>
    private InMemoryStagedVersion? ThrowIfNotAt(ResourceAddress resource, string versionId, long offset)
    {
        if (StagedOf(resource, versionId) is not { } staged) return null;
        if (staged.Content.Length != offset)
        {
            throw new ResourceUploadOffsetException($"The upload {versionId} of the resource {resource.ResourceName} in the " +
                $"container {resource.ContainerName} has {staged.Content.Length} bytes stored, not {offset}.", staged.Content.Length)
            {
                ContainerName = resource.ContainerName, ResourceName = resource.ResourceName
            };
        }
        return staged;
    }

    public override Task<bool> DeleteContainerAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() =>
        {
            if (!_containers.TryGetValue(resourceContainerName, out InMemoryResourceContainer? container)) return false;
            ThrowIfAnyRetained(container.Resources.Values.SelectMany(versions => versions));
            return _containers.Remove(resourceContainerName);
        }, cancellationToken);

    public override Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        UnderLockAsync(() => _containers.GetValueOrDefault(resourceContainerName) is { } container
            ? new ResourceContainerUsage(container.Resources.Values.Sum(versions => versions.Sum(v => (long)v.Content.Length)),
                container.Resources.Count, container.Resources.Values.Sum(versions => (long)versions.Count),
                container.Deleted.Values.Sum(bin => bin.Sum(v => (long)v.Content.Length)), container.Deleted.Values.Sum(bin => (long)bin.Count))
            : new ResourceContainerUsage(0, 0, 0), cancellationToken);

    private async Task<T> UnderLockAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Throws for the first of <paramref name="versions"/> that can't be deleted now; called under the lock.</summary>
    private void ThrowIfAnyRetained(IEnumerable<InMemoryResource> versions)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        if (versions.Select(v => v.ResourceInfo).FirstOrDefault(v => !v.IsDeletableAt(now)) is { } retained)
        {
            throw ResourceRetainedException.Create(new ResourceAddress(retained.ContainerName, retained.Name), retained.VersionId,
                retained.RetainUntil, retained.LegalHold, now);
        }
    }

    private static ArgumentOutOfRangeException RetentionShortened(ResourceAddress resource, ResourceInfo version,
        DateTimeOffset retainUntil) =>
        new("retainUntil", retainUntil, $"The version {version.VersionId} of the resource {resource.ResourceName} in the " +
            $"container {resource.ContainerName} is retained until {version.RetainUntil:O}, and that can't move earlier.");

    /// <summary>A name with no current version meets no condition; empty conditions are no conditions.</summary>
    private static ResourceContainerNotFoundException ContainerMissing(string resourceContainerName) =>
        new($"The container {resourceContainerName} doesn't exist.") { ContainerName = resourceContainerName };

    private static ResourceConflictException ResourceExists(string resourceContainerName, string resourceName) =>
        new($"The resource {resourceName} already exists in the container {resourceContainerName}.")
        {
            ContainerName = resourceContainerName, ResourceName = resourceName
        };

    private static void ThrowIfNotMet(ResourceConditions conditions, string resourceContainerName, string resourceName,
        InMemoryResource? current) =>
        conditions.ThrowIfNotMet(new ResourceAddress(resourceContainerName, resourceName), current?.ResourceInfo.VersionId,
            current?.ResourceInfo.ContentHash);

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
