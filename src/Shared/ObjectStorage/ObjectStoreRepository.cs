using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Resourcefull.Providers;

namespace Resourcefull.ObjectStorage;

/// <summary>
/// Keeps resources in an object store, using only conditional writes for consistency, so it works on any store
/// <see cref="IObjectStore"/> can reach. Each container has a record, and each name a head object listing its versions'
/// attributes in the order they were stored, the last one current; every change to a name is a compare-and-swap of its
/// head, retried when another writer got there first. Content goes in objects of its own, written before the head names
/// them. See <see cref="ObjectKeys"/> for the layout.
/// </summary>
/// <remarks>
/// Every conditional write carries a random write ID. When the store refuses one, the object is read again: if it
/// holds that ID, the write did land, and the refusal was the client sending it again after its answer was lost.
/// </remarks>
/// <param name="store">The store, reached through its provider's client.</param>
/// <param name="keyPrefix">What every key starts with.</param>
/// <param name="timeProvider">The clock versions are stamped with.</param>
/// <param name="storeName">The store's name, for telemetry; <see langword="null"/> for the store without one.</param>
internal sealed partial class ObjectStoreRepository(IObjectStore store, string keyPrefix, TimeProvider? timeProvider, string? storeName)
    : ResourceRepository
{
    /// <summary>
    /// The real time, to tell an expiry entry's age by: the store stamps entries with it, while the clock versions are
    /// stamped with may be set to another time. Tests set it.
    /// </summary>
    internal TimeProvider RealTime { get; init; } = TimeProvider.System;

    /// <summary>How many heads or records a listing reads at once.</summary>
    private const int ParallelReads = 16;

    /// <summary>How many names <see cref="GetAllResourceInfoAsync"/> lists at a time.</summary>
    private const int ListAllPageSize = 1000;

    /// <summary>
    /// How many times in a row a conditional write may be refused before it fails: a store that keeps refusing one
    /// most likely doesn't honor the conditions at all.
    /// </summary>
    private const int MaxAttempts = 100;

    /// <summary>
    /// The size of the head a name keeps once all its versions are deleted: just the write ID. A head with versions is
    /// JSON, and always larger.
    /// </summary>
    private const int EmptyHeadSize = 32;

    /// <summary>How many objects a purge lists, then deletes, at a time.</summary>
    private const int PurgeBatchSize = 1000;

    /// <summary>
    /// How old an expiry entry that names no version with its expiry must be before it's dropped. An entry goes in just
    /// before the head write that names its version, so a younger one may belong to a write still in progress.
    /// </summary>
    private static readonly TimeSpan StaleExpiryAge = TimeSpan.FromHours(1);

    private readonly ObjectKeys _keys = new(keyPrefix);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // Resources

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
    private async Task<WrittenContent> AddVersionAsync(NewResourceVersion version, Stream content, ResourceWriteCondition condition,
        DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        ResourceLocation location = version.Location;
        ContainerRecord container = await GetContainerAsync(location.ContainerName, cancellationToken).ConfigureAwait(false)
            ?? throw ContainerMissing(location.ContainerName);
        string headKey = _keys.Head(container.Id, location.ResourceName);

        // Checked again by the head write; this only spares uploading content that would be refused.
        if (condition.CreateOnly || condition.Conditions is not null)
        {
            Head? existing = await ReadHeadAsync(headKey, cancellationToken).ConfigureAwait(false);
            ThrowUnlessAddable(location, existing, version.VersionId, condition);
        }

        string blob = Guid.NewGuid().ToString("N");
        string contentKey = _keys.Content(container.Id, blob);
        string? expiryToken = version.ExpiresAt is null ? null : Guid.NewGuid().ToString("N");
        string? expiryKey = version.ExpiresAt is { } expiresAt ? _keys.Expiry(expiresAt, version.VersionId, expiryToken!, container.Id, blob) : null;
        using var hashing = new HashingStream(content);
        WrittenContent? written = null;
        try
        {
            await store.UploadAsync(contentKey, hashing, cancellationToken).ConfigureAwait(false);
            if (expiryKey is not null) await PutExpiryAsync(expiryKey, location, cancellationToken).ConfigureAwait(false);
            await ChangeHeadAsync(headKey, head =>
            {
                // An earlier try already added this version: it landed, but another writer changed the head before it
                // could be confirmed.
                if (head?.Versions.Any(v => v.Blob == blob) == true) return null;
                ThrowUnlessAddable(location, head, version.VersionId, condition);
                // Stamped as the version becomes current. A wrong declared length throws here, before the head names it.
                written = hashing.Complete(version.DeclaredLength, lastModified ?? _clock.GetUtcNow());
                return new Head([.. head?.Versions ?? [], new StoredVersion(version.VersionId, blob, written.ContentLength,
                    written.ContentHash, version.ContentType, new Dictionary<string, string>(version.Metadata),
                    written.LastModified, version.ExpiresAt, version.CopiedFrom, expiryToken)]);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteUnnamedContentAsync(headKey, blob, expiryKey is null ? [contentKey] : [contentKey, expiryKey]).ConfigureAwait(false);
            throw;
        }

        // A container deleted while the version was stored: the head went in under the container's old ID, which
        // nothing reads anymore, so whatever is left there is deleted.
        if ((await GetContainerAsync(location.ContainerName, CancellationToken.None).ConfigureAwait(false))?.Id != container.Id)
        {
            await PurgeQuietlyAsync(container.Id).ConfigureAwait(false);
            throw ContainerMissing(location.ContainerName);
        }
        return written!;
    }

    private static void ThrowUnlessAddable(ResourceLocation location, Head? head, string versionId, ResourceWriteCondition condition)
    {
        StoredVersion? current = head?.Versions[^1];
        if (condition.CreateOnly && current is not null) throw ResourceExists(location);
        if (head?.Versions.Any(v => v.VersionId == versionId) == true)
        {
            throw new ResourceConflictException($"The resource {location.ResourceName} in the container " +
                $"{location.ContainerName} already has a version {versionId}.")
            {
                ContainerName = location.ContainerName, ResourceName = location.ResourceName
            };
        }
        condition.Conditions?.ThrowIfNotMet(location, current?.VersionId, current?.ContentHash);
    }

    /// <summary>
    /// Deletes the content of a write that failed, and its expiry entry if it has one, unless the head names it after
    /// all, as it does when the head write landed and what failed came after. If the head can't be read, they stay.
    /// </summary>
    private async Task DeleteUnnamedContentAsync(string headKey, string blob, string[] keys)
    {
        try
        {
            Head? head = (await GetAsync<Head>(headKey, CancellationToken.None).ConfigureAwait(false)).Value;
            if (head?.Versions.Any(v => v.Blob == blob) != true) await DeleteQuietlyAsync(keys).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // Left in place: deleting content a head names would lose a stored version.
        }
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        var all = new List<ResourceInfo>();
        IReadOnlyList<ResourceInfo> page;
        do
        {
            page = await GetResourcePageAsync(resourceContainerName, null, all.LastOrDefault()?.Name, ListAllPageSize, cancellationToken)
                .ConfigureAwait(false);
            all.AddRange(page);
        }
        while (page.Count == ListAllPageSize);
        return all;
    }

    /// <summary>Lists the names' heads in key order, which is their names' code point order, and reads them.</summary>
    public override async Task<IReadOnlyList<ResourceInfo>> GetResourcePageAsync(string resourceContainerName, string? prefix,
        string? afterName, int pageSize, CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is not { } container)
            return [];

        string heads = _keys.Heads(container.Id);
        var page = new List<ResourceInfo>();
        await foreach (string[] keys in ListKeysAsync(_keys.HeadsStartingWith(container.Id, prefix),
            afterName is null ? null : _keys.Head(container.Id, afterName), item => item.Size > EmptyHeadSize,
            () => pageSize - page.Count, cancellationToken).ConfigureAwait(false))
        {
            foreach ((string key, Head? read) in await ReadAllAsync<Head>(keys, cancellationToken).ConfigureAwait(false))
            {
                Head? head = read?.Move is null ? read : await ReadHeadAsync(key, cancellationToken).ConfigureAwait(false);
                // A name whose versions were deleted, or moved, since it was listed is left out.
                if (head is not null)
                    page.Add(ToInfo(resourceContainerName, ObjectKeys.Unescape(key[heads.Length..]), head.Versions[^1], true));
            }
            if (page.Count >= pageSize) break;
        }
        return page;
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceLocation resource,
        CancellationToken cancellationToken = default)
    {
        if (await GetHeadAsync(resource, cancellationToken).ConfigureAwait(false) is not { } head) return [];
        return [.. head.Versions.Select((v, i) => ToInfo(resource.ContainerName, resource.ResourceName, v, i == head.Versions.Count - 1))
            .Reverse()];
    }

    public override async Task<ResourceInfo?> GetResourceInfoAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        if (await GetHeadAsync(resource, cancellationToken).ConfigureAwait(false) is not { } head) return null;
        StoredVersion current = head.Versions[^1];
        StoredVersion? version = versionId is null ? current : head.Versions.FirstOrDefault(v => v.VersionId == versionId);
        return version is null ? null : ToInfo(resource.ContainerName, resource.ResourceName, version, version == current);
    }

    public override Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        GetResourceStreamAsync(resource, versionId, 0, null, cancellationToken);

    /// <summary>Opens a version's content from <paramref name="offset"/>, asking the store for only the range read.</summary>
    public override async Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId, long offset,
        long? length, CancellationToken cancellationToken = default)
    {
        ContainerRecord? container = await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false);
        if (container is null) return null;
        Head? head = await ReadHeadAsync(_keys.Head(container.Id, resource.ResourceName), cancellationToken).ConfigureAwait(false);
        StoredVersion? version = versionId is null ? head?.Versions[^1] : head?.Versions.FirstOrDefault(v => v.VersionId == versionId);
        if (version is null) return null;

        long start = Math.Min(offset, version.ContentLength);
        long end = Math.Min(length is { } count ? start + count : version.ContentLength, version.ContentLength);
        if (start >= end) return new ObjectResourceStream(null, version.ContentLength, start);

        ObjectRead? read = await store.OpenReadAsync(_keys.Content(container.Id, version.Blob), start, end, version.ContentLength,
            cancellationToken).ConfigureAwait(false);
        // Missing when it was deleted since the head was read.
        return read is null ? null : new ObjectResourceStream(read, version.ContentLength, start);
    }

    /// <summary>
    /// Takes the versions off the name's head in one write, deciding the conditions there, then deletes their content.
    /// Content whose delete fails is no longer named by any head, and stays until the container is deleted.
    /// </summary>
    public override async Task<bool> DeleteResourceAsync(ResourceLocation resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default)
    {
        ContainerRecord? container = await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            conditions?.ThrowIfNotMet(resource, null, null);
            return false;
        }

        var removed = new List<StoredVersion>();
        bool changed = await ChangeHeadAsync(_keys.Head(container.Id, resource.ResourceName), head =>
        {
            removed.Clear();
            StoredVersion? current = head?.Versions[^1];
            conditions?.ThrowIfNotMet(resource, current?.VersionId, current?.ContentHash);
            if (head is null) return null;
            removed.AddRange(versionId is null ? head.Versions : head.Versions.Where(v => v.VersionId == versionId));
            return removed.Count == 0 ? null : new Head([.. head.Versions.Except(removed)]);
        }, cancellationToken).ConfigureAwait(false);
        if (!changed) return false;

        await DeleteQuietlyAsync([.. removed.SelectMany(v => KeysOf(container.Id, v))]).ConfigureAwait(false);
        return true;
    }

    /// <summary>A version's content key, and its expiry entry's key if it has an expiry.</summary>
    private IEnumerable<string> KeysOf(string containerId, StoredVersion version)
    {
        yield return _keys.Content(containerId, version.Blob);
        if (ExpiryKeyOf(containerId, version) is { } expiryKey) yield return expiryKey;
    }

    /// <summary>A version's expiry entry's key, or <see langword="null"/> when it has no expiry.</summary>
    private string? ExpiryKeyOf(string containerId, StoredVersion version) =>
        version is { ExpiresAt: { } expiresAt, ExpiryToken: { } token }
            ? _keys.Expiry(expiresAt, version.VersionId, token, containerId, version.Blob)
            : null;

    public override Task<bool> SetResourceMetadataAsync(ResourceLocation resource, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        ChangeVersionAsync(resource, versionId, v => v with { Metadata = new Dictionary<string, string>(metadata) }, cancellationToken);

    public override Task<bool> SetContentTypeAsync(ResourceLocation resource, string? versionId, string? contentType,
        CancellationToken cancellationToken = default) =>
        ChangeVersionAsync(resource, versionId, v => v with { ContentType = contentType }, cancellationToken);

    private async Task<bool> ChangeVersionAsync(ResourceLocation resource, string? versionId, Func<StoredVersion, StoredVersion> change,
        CancellationToken cancellationToken)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return false;
        return await ChangeHeadAsync(_keys.Head(container.Id, resource.ResourceName), head =>
        {
            int index = head is null ? -1 : versionId is null ? head.Versions.Count - 1 : head.Versions.FindIndex(v => v.VersionId == versionId);
            if (index < 0) return null;
            List<StoredVersion> versions = [.. head!.Versions];
            versions[index] = change(versions[index]);
            return new Head(versions);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds up the container's content objects' sizes, and counts the names with versions, from listings alone.
    /// Content an unfinished write left behind counts until the container is deleted, and while a move is being made, a
    /// name it moves counts in both places.
    /// </summary>
    public override async Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is not { } container)
            return new ResourceContainerUsage(0, 0, 0);

        long storedBytes = 0, versions = 0, resources = 0;
        await foreach (ObjectListing content in store.ListAsync(_keys.Contents(container.Id), null, cancellationToken).ConfigureAwait(false))
        {
            storedBytes += content.Size;
            versions++;
        }
        await foreach (ObjectListing head in store.ListAsync(_keys.Heads(container.Id), null, cancellationToken).ConfigureAwait(false))
        {
            if (head.Size > EmptyHeadSize) resources++;
        }
        return new ResourceContainerUsage(storedBytes, resources, versions);
    }

    // Expiry: an index of the versions that expire, one entry each under {prefix}x/, keyed by expiry time, so a sweep
    // lists only what's due. An entry goes in before the head write that sets the expiry, so a version never has an
    // expiry without one. Each entry's key holds a token the version records, so no two writes share a key, and an
    // entry whose version no longer records its token is stale. Changing the expiry or deleting the version deletes the
    // old entry; one those deletes miss is dropped when a listing finds it over an hour old.

    public override async Task<IReadOnlyList<ResourceInfo>> GetExpiredAsync(DateTimeOffset now, int maxVersions,
        (DateTimeOffset ExpiresAt, string VersionId)? after = null, CancellationToken cancellationToken = default)
    {
        string? startAfter = after is { } last ? _keys.ExpiriesAfter(last.ExpiresAt, last.VersionId) : null;
        var expired = new List<ResourceInfo>();
        var batch = new List<ObjectListing>();
        await foreach (ObjectListing entry in store.ListAsync(_keys.Expiries, startAfter, cancellationToken).ConfigureAwait(false))
        {
            if (ObjectKeys.ParseExpiry(entry.Key[_keys.Expiries.Length..]).ExpiresAt > now) break;
            batch.Add(entry);
            if (batch.Count < Math.Min(maxVersions - expired.Count, ParallelReads)) continue;
            expired.AddRange(await ReadExpiredAsync(batch, cancellationToken).ConfigureAwait(false));
            batch.Clear();
            if (expired.Count >= maxVersions) return expired;
        }
        expired.AddRange(await ReadExpiredAsync(batch, cancellationToken).ConfigureAwait(false));
        return expired;
    }

    /// <summary>The versions a batch of expiry entries name, in the entries' order, leaving out stale entries.</summary>
    private async Task<IEnumerable<ResourceInfo>> ReadExpiredAsync(List<ObjectListing> entries, CancellationToken cancellationToken)
    {
        var versions = new ResourceInfo?[entries.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (i, ct) => versions[i] = await ReadExpiredAsync(entries[i], ct).ConfigureAwait(false)).ConfigureAwait(false);
        return versions.OfType<ResourceInfo>();
    }

    /// <summary>
    /// The version an expiry entry names, if it still has that expiry; otherwise <see langword="null"/>, and an entry old
    /// enough to belong to no write in progress is dropped.
    /// </summary>
    private async Task<ResourceInfo?> ReadExpiredAsync(ObjectListing entry, CancellationToken cancellationToken)
    {
        (_, string versionId, string token, string containerId, string blob) = ObjectKeys.ParseExpiry(entry.Key[_keys.Expiries.Length..]);
        if ((await GetAsync<ExpiryEntry>(entry.Key, cancellationToken).ConfigureAwait(false)).Value is not { } named) return null;
        ContainerRecord? container = await GetContainerAsync(named.ContainerName, cancellationToken).ConfigureAwait(false);
        Head? head = container?.Id == containerId
            ? await ReadHeadAsync(_keys.Head(containerId, named.ResourceName), cancellationToken).ConfigureAwait(false)
            : null;
        StoredVersion? version = head?.Versions.FirstOrDefault(v => v.Blob == blob);
        if (version is not null && version.VersionId == versionId && version.ExpiryToken == token)
            return ToInfo(named.ContainerName, named.ResourceName, version, version == head!.Versions[^1]);

        if (entry.LastModified < RealTime.GetUtcNow() - StaleExpiryAge)
        {
            await DeleteQuietlyAsync([entry.Key]).ConfigureAwait(false);
            ObjectStoreTelemetry.CountStaleExpiry(storeName);
        }
        return null;
    }

    /// <summary>
    /// Takes the version off its name's head only if its expiry is still due, checked within the conditional head write,
    /// then deletes its content and its expiry entry.
    /// </summary>
    public override async Task<bool> DeleteExpiredVersionAsync(ResourceLocation resource, string versionId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return false;
        StoredVersion? removed = null;
        bool changed = await ChangeHeadAsync(_keys.Head(container.Id, resource.ResourceName), head =>
        {
            removed = head?.Versions.FirstOrDefault(v => v.VersionId == versionId);
            if (removed is not { ExpiresAt: { } expiresAt } || expiresAt > now) return null;
            return new Head([.. head!.Versions.Where(v => v.VersionId != versionId)]);
        }, cancellationToken).ConfigureAwait(false);
        if (!changed) return false;

        await DeleteQuietlyAsync([.. KeysOf(container.Id, removed!)]).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Writes an expiry entry for the new time, if there is one, under a new token, then sets both on the version with a
    /// head write, and deletes the old entry, which nothing names anymore.
    /// </summary>
    public override async Task<bool> SetExpiryAsync(ResourceLocation resource, string? versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return false;
        string headKey = _keys.Head(container.Id, resource.ResourceName);
        // A version's blob never changes, so the entry can be written for the version as it's read now.
        Head? read = await ReadHeadAsync(headKey, cancellationToken).ConfigureAwait(false);
        StoredVersion? target = versionId is null ? read?.Versions[^1] : read?.Versions.FirstOrDefault(v => v.VersionId == versionId);
        if (target is null) return false;
        string? token = expiresAt is null ? null : Guid.NewGuid().ToString("N");
        if (expiresAt is { } time)
        {
            await PutExpiryAsync(_keys.Expiry(time, target.VersionId, token!, container.Id, target.Blob), resource, cancellationToken)
                .ConfigureAwait(false);
        }

        StoredVersion? replaced = null;
        bool changed = await ChangeHeadAsync(headKey, head =>
        {
            int index = head?.Versions.FindIndex(v => v.Blob == target.Blob) ?? -1;
            if (index < 0) return null;
            List<StoredVersion> versions = [.. head!.Versions];
            replaced = versions[index];
            versions[index] = replaced with { ExpiresAt = expiresAt, ExpiryToken = token };
            return new Head(versions);
        }, cancellationToken).ConfigureAwait(false);
        if (changed && ExpiryKeyOf(container.Id, replaced!) is { } replacedKey) await DeleteQuietlyAsync([replacedKey]).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Writes an expiry entry holding the version's container and resource names, since its key has only the container's
    /// ID and the version's blob.
    /// </summary>
    private Task PutExpiryAsync(string key, ResourceLocation resource, CancellationToken cancellationToken) =>
        store.UploadAsync(key, new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new ExpiryEntry(resource.ContainerName,
            resource.ResourceName))), cancellationToken);

    // Resource Containers

    public override async Task AddContainerAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        string key = _keys.Container(resourceContainerName);
        var record = new ContainerRecord(Guid.NewGuid().ToString("N"), new Dictionary<string, string>(metadata));
        for (int attempt = 1; ; attempt++)
        {
            Stored<ContainerRecord> existing = await GetAsync<ContainerRecord>(key, cancellationToken).ConfigureAwait(false);
            // An earlier try already created it: it landed, but another writer changed the record before it could be
            // confirmed.
            if (existing.Value?.Id == record.Id) return;
            if (existing.Value is { Deleted: false })
            {
                throw new ResourceConflictException($"The container {resourceContainerName} already exists.")
                {
                    ContainerName = resourceContainerName
                };
            }
            // A deleted container's record holds the only note of its old ID, so what's left under it is deleted first.
            if (existing.Value is { } deleted) await PurgeAsync(deleted.Id, cancellationToken).ConfigureAwait(false);
            if (await TryPutAsync(key, id => Serialize(record with { WriteId = id }), existing.ETag).ConfigureAwait(false)) return;
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = new List<ContainerInfo>();
        await foreach (string[] keys in ListKeysAsync(_keys.ContainersStartingWith(prefix),
            afterName is null ? null : _keys.Container(afterName), _ => true, () => pageSize - page.Count, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach ((string key, ContainerRecord? record) in await ReadAllAsync<ContainerRecord>(keys, cancellationToken).ConfigureAwait(false))
            {
                if (record is { Deleted: false })
                    page.Add(ToInfo(ObjectKeys.Unescape(key[_keys.Containers.Length..]), record));
            }
            if (page.Count >= pageSize) break;
        }
        return page;
    }

    public override async Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        await GetContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is { } record
            ? ToInfo(resourceContainerName, record)
            : null;

    /// <summary>
    /// Marks the container's record deleted, which hides everything in it at once, then deletes what it held. If that
    /// stops partway, deleting the container again finishes it.
    /// </summary>
    public override async Task<bool> DeleteContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        string key = _keys.Container(resourceContainerName);
        string? deleting = null;
        for (int attempt = 1; ; attempt++)
        {
            Stored<ContainerRecord> existing = await GetAsync<ContainerRecord>(key, cancellationToken).ConfigureAwait(false);
            if (existing.Value is not { } record) return false;
            // An earlier try already marked it deleted: it landed, but before that could be confirmed, the container was
            // created again under a new ID, which mustn't be deleted.
            if (deleting is not null && record.Id != deleting)
            {
                await PurgeAsync(deleting, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (record.Deleted)
            {
                await PurgeAsync(record.Id, cancellationToken).ConfigureAwait(false);
                return false;
            }
            deleting = record.Id;
            if (await TryPutAsync(key, id => Serialize(record with { Deleted = true, WriteId = id }), existing.ETag).ConfigureAwait(false))
            {
                await PurgeAsync(record.Id, cancellationToken).ConfigureAwait(false);
                return true;
            }
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<bool> SetContainerMetadataAsync(string resourceContainerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        string key = _keys.Container(resourceContainerName);
        for (int attempt = 1; ; attempt++)
        {
            Stored<ContainerRecord> existing = await GetAsync<ContainerRecord>(key, cancellationToken).ConfigureAwait(false);
            if (existing.Value is not { Deleted: false } record) return false;
            ContainerRecord changed = record with { Metadata = new Dictionary<string, string>(metadata) };
            if (await TryPutAsync(key, id => Serialize(changed with { WriteId = id }), existing.ETag).ConfigureAwait(false)) return true;
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    // Storage

    private async Task<ContainerRecord?> GetContainerAsync(string resourceContainerName, CancellationToken cancellationToken) =>
        (await GetAsync<ContainerRecord>(_keys.Container(resourceContainerName), cancellationToken).ConfigureAwait(false)).Value
            is { Deleted: false } record ? record : null;

    private async Task<Head?> GetHeadAsync(ResourceLocation resource, CancellationToken cancellationToken) =>
        await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is { } container
            ? await ReadHeadAsync(_keys.Head(container.Id, resource.ResourceName), cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Changes a name's head with <paramref name="change"/>, which gets the head (or <see langword="null"/> for a name
    /// with no versions) and returns the new one, or <see langword="null"/> to change nothing. When another writer
    /// changes the head first, it reads it again and calls <paramref name="change"/> again. For a head a move marked,
    /// <paramref name="change"/> gets the head as the move decided it, after abandoning the move if it's still pending.
    /// </summary>
    /// <returns>Whether the head changed.</returns>
    private async Task<bool> ChangeHeadAsync(string key, Func<Head?, Head?> change, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            (Stored<Head> current, Head? head) = await ReadHeadToChangeAsync(key, cancellationToken).ConfigureAwait(false);
            if (change(head) is not { } changed) return false;
            if (await TryPutAsync(key, id => Serialize(changed with { WriteId = id, Move = null }), current.ETag).ConfigureAwait(false))
                return true;

            ObjectStoreTelemetry.CountRetry(storeName);
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits a little before writing <paramref name="key"/> again, at random once writers keep colliding, so one of them
    /// gets through; and gives up once the store has refused <see cref="MaxAttempts"/> writes in a row.
    /// </summary>
    private async Task WaitToRetryAsync(string key, int attempt, CancellationToken cancellationToken)
    {
        if (attempt >= MaxAttempts)
        {
            throw new InvalidOperationException($"The store refused {MaxAttempts} conditional writes of {key} in a row. " +
                $"Check that it honors {store.ConditionalWrites}, and that nothing else writes under Resourcefull's prefix.");
        }
        if (attempt > 2) await Task.Delay(Random.Shared.Next(1, Math.Min(10 * attempt, 200)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An object's value, and its ETag for a conditional write; both <see langword="null"/> when there's no object.
    /// A head with no versions, which holds only a write ID, has an ETag and no value.
    /// </summary>
    private async Task<Stored<T>> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        (byte[]? body, string? etag) = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
        return new Stored<T>(body is { Length: > 0 } && body[0] == '{' ? JsonSerializer.Deserialize<T>(body) : null, etag);
    }

    /// <summary>
    /// Writes the body <paramref name="body"/> makes from a new write ID, only if the object is still as it was read:
    /// with the ETag it had, or absent when <paramref name="etag"/> is <see langword="null"/>. Once sent, a write isn't
    /// canceled, so a caller that cancels never leaves it unknown whether the write landed.
    /// </summary>
    /// <returns>Whether it was written; <see langword="false"/> when another writer changed the object first.</returns>
    private async Task<bool> TryPutAsync(string key, Func<string, byte[]> body, string? etag)
    {
        string writeId = Guid.NewGuid().ToString("N");
        // When refused, the write may have landed and its response been lost, and the store then refused the client's
        // resend because of it.
        return await store.TryPutAsync(key, body(writeId), etag).ConfigureAwait(false)
            || await ReadWriteIdAsync(key).ConfigureAwait(false) == writeId;
    }

    /// <summary>The write ID an object holds: in its JSON, or as the whole of a head with no versions.</summary>
    private async Task<string?> ReadWriteIdAsync(string key)
    {
        (byte[]? body, _) = await store.GetAsync(key, CancellationToken.None).ConfigureAwait(false);
        if (body is null) return null;
        if (body.Length == 0 || body[0] != '{') return Encoding.UTF8.GetString(body);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("w", out JsonElement id) ? id.GetString() : null;
    }

    /// <summary>
    /// Lists the objects under <paramref name="prefix"/> after <paramref name="startAfter"/> that <paramref name="live"/>
    /// keeps, in key order, a batch at a time: as many as <paramref name="wanted"/> says are still needed, up to what's
    /// read at once.
    /// </summary>
    private async IAsyncEnumerable<string[]> ListKeysAsync(string prefix, string? startAfter, Func<ObjectListing, bool> live,
        Func<int> wanted, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new List<string>();
        await foreach (ObjectListing item in store.ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            if (!live(item)) continue;
            batch.Add(item.Key);
            if (batch.Count >= Math.Clamp(wanted(), 1, ParallelReads * 4))
            {
                yield return [.. batch];
                batch.Clear();
            }
        }
        if (batch.Count > 0) yield return [.. batch];
    }

    /// <summary>Reads every key in <paramref name="keys"/>, several at once, keeping their order.</summary>
    private async Task<(string Key, T? Value)[]> ReadAllAsync<T>(string[] keys, CancellationToken cancellationToken) where T : class
    {
        var values = new (string, T?)[keys.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, keys.Length),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (i, ct) => values[i] = (keys[i], (await GetAsync<T>(keys[i], ct).ConfigureAwait(false)).Value)).ConfigureAwait(false);
        return values;
    }

    /// <summary>Deletes everything under a container's ID, a batch at a time, each listed afresh.</summary>
    private async Task PurgeAsync(string containerId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var keys = new List<string>(PurgeBatchSize);
            await foreach (ObjectListing item in store.ListAsync(_keys.Data(containerId), null, cancellationToken).ConfigureAwait(false))
            {
                keys.Add(item.Key);
                if (keys.Count == PurgeBatchSize) break;
            }
            if (keys.Count == 0) return;
            await store.DeleteAsync(keys, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes what a write left under a deleted container's ID. If that fails, what's left stays, but nothing reads it.
    /// </summary>
    private async Task PurgeQuietlyAsync(string containerId)
    {
        try
        {
            await PurgeAsync(containerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // Nothing reads under a deleted container's ID.
        }
    }

    /// <summary>
    /// Deletes content no head names anymore. If that fails, the content stays until its container is deleted.
    /// </summary>
    private async Task DeleteQuietlyAsync(string[] keys)
    {
        if (keys.Length == 0) return;
        try
        {
            await store.DeleteAsync(keys, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // Unreachable either way: nothing names it.
        }
    }

    /// <summary>A head as stored: JSON, or for a name with no versions, just the write ID.</summary>
    private static byte[] Serialize(Head head) =>
        head.Versions.Count == 0 ? Encoding.UTF8.GetBytes(head.WriteId!) : JsonSerializer.SerializeToUtf8Bytes(head);

    private static byte[] Serialize(ContainerRecord record) => JsonSerializer.SerializeToUtf8Bytes(record);

    private static ResourceInfo ToInfo(string containerName, string name, StoredVersion version, bool isLatest) => new()
    {
        ContainerName = containerName,
        Name = name,
        VersionId = version.VersionId,
        LastModified = version.LastModified,
        IsLatest = isLatest,
        ContentType = version.ContentType,
        ContentLength = version.ContentLength,
        ContentHash = version.ContentHash,
        Metadata = new Dictionary<string, string>(version.Metadata),
        CopiedFrom = version.CopiedFrom,
        ExpiresAt = version.ExpiresAt
    };

    private static ContainerInfo ToInfo(string name, ContainerRecord record) => new()
    {
        Name = name, Metadata = new Dictionary<string, string>(record.Metadata)
    };

    private static ResourceConflictException ResourceExists(ResourceLocation location) =>
        new($"The resource {location.ResourceName} already exists in the container {location.ContainerName}.")
        {
            ContainerName = location.ContainerName, ResourceName = location.ResourceName
        };

    private static ResourceContainerNotFoundException ContainerMissing(string resourceContainerName) =>
        new($"The container {resourceContainerName} doesn't exist.") { ContainerName = resourceContainerName };

    /// <summary>An object's value and ETag, as read.</summary>
    private readonly record struct Stored<T>(T? Value, string? ETag) where T : class;

    /// <summary>
    /// A container's record: its ID, which the keys of what it holds start with, its metadata, and whether it's deleted.
    /// </summary>
    private sealed record ContainerRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("m")] Dictionary<string, string> Metadata,
        [property: JsonPropertyName("deleted")] bool Deleted = false,
        [property: JsonPropertyName("w")] string? WriteId = null);

    /// <summary>What an expiry entry names: the container and resource of the version its key identifies.</summary>
    private sealed record ExpiryEntry(
        [property: JsonPropertyName("c")] string ContainerName,
        [property: JsonPropertyName("n")] string ResourceName);

    /// <summary>
    /// A name's versions, in the order they were stored; the last is current. A head a move is changing holds the move's
    /// ID, and the move's entry decides whether the versions are here.
    /// </summary>
    private sealed record Head(
        [property: JsonPropertyName("v")] List<StoredVersion> Versions,
        [property: JsonPropertyName("w")] string? WriteId = null,
        [property: JsonPropertyName("mv"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HeadMove? Move = null);

    /// <summary>One version's attributes, and the key of its content.</summary>
    private sealed record StoredVersion(
        [property: JsonPropertyName("id")] string VersionId,
        [property: JsonPropertyName("b")] string Blob,
        [property: JsonPropertyName("n")] long ContentLength,
        [property: JsonPropertyName("h")] string ContentHash,
        [property: JsonPropertyName("t")] string? ContentType,
        [property: JsonPropertyName("m")] Dictionary<string, string> Metadata,
        [property: JsonPropertyName("at")] DateTimeOffset LastModified,
        [property: JsonPropertyName("x")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("o")] ResourceOrigin? CopiedFrom,
        [property: JsonPropertyName("xt")] string? ExpiryToken = null);
}
