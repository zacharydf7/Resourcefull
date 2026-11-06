using Resourcefull.Providers;

namespace Resourcefull.ObjectStorage;

// The bin: a deleted version moves from its name's versions to the head's bin, in the head write that deletes it, and its
// content stays where it is. Two indexes find it there, each with one empty entry per version: a bin entry under its
// container's data, newest deletion first, for listings and the usage figures, and a purge entry under {prefix}t/, by
// purge time, for the sweep. Both go in before the head write that puts the version in the bin, under a token the
// version records, and are checked against the head when read, as expiry entries are; an entry whose version doesn't
// record its token is stale. A listing that reaches it drops it once it's over an hour old, and the daily scan for
// unnamed content drops one older than its cutoff. A name left with only a bin has a mark beside its head, so the usage
// figures can leave it out of the names; the scan fixes any mark a failed write left wrong.
internal sealed partial class ObjectStoreRepository
{
    /// <summary>
    /// How many bin entries one page of a listing with a prefix looks at before it returns what it found, so a prefix
    /// few deleted names match can't make one page read the whole bin.
    /// </summary>
    private const int MaxBinEntriesExamined = 1000;

    public override bool SupportsSoftDelete => true;

    /// <summary>
    /// Writes the versions' bin and purge entries, then moves them to the bin in one head write that decides the
    /// conditions and retention, and deletes their expiry entries, since expiry leaves the bin alone. When another writer
    /// changes the head so the delete takes a version whose entries weren't written, it starts again.
    /// </summary>
    public override async Task<bool> SoftDeleteResourceAsync(ResourceAddress resource, string? versionId, ResourceConditions? conditions,
        DateTimeOffset purgeAt, CancellationToken cancellationToken = default)
    {
        ContainerRecord? container = await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            conditions?.ThrowIfNotMet(resource, null, null);
            return false;
        }

        DateTimeOffset deletedAt = _clock.GetUtcNow();
        // The tokens of the versions whose entries are written, by blob, which never changes.
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = new List<string>();
        var removed = new List<StoredVersion>();
        bool changed;
        try
        {
            while (true)
            {
                Head? read = await ReadHeadAsync(_keys.Head(container.Id, resource.ResourceName), cancellationToken).ConfigureAwait(false);
                foreach (StoredVersion version in read?.Versions.Where(v => versionId is null || v.VersionId == versionId) ?? [])
                {
                    if (tokens.ContainsKey(version.Blob)) continue;
                    string token = Guid.NewGuid().ToString("N");
                    string[] keys = [.. BinKeysOf(container.Id, version with { DeletedAt = deletedAt, PurgeAt = purgeAt, BinToken = token })];
                    foreach (string key in keys) await PutEntryAsync(key, resource, cancellationToken).ConfigureAwait(false);
                    written.AddRange(keys);
                    tokens[version.Blob] = token;
                }

                bool unwritten = false;
                changed = await ChangeHeadKeepingSearchAsync(container.Id, resource.ResourceName, head =>
                {
                    // Called for a dry run first when metadata is searchable, so each call decides afresh.
                    unwritten = false;
                    removed.Clear();
                    StoredVersion? current = head?.Versions.LastOrDefault();
                    conditions?.ThrowIfNotMet(resource, current?.VersionId, current?.ContentHash);
                    if (current is null) return null;
                    removed.AddRange(versionId is null ? head!.Versions : head!.Versions.Where(v => v.VersionId == versionId));
                    if (removed.FirstOrDefault(v => !IsDeletable(v, deletedAt)) is { } retained)
                        throw ResourceRetainedException.Create(resource, retained.VersionId, retained.RetainUntil, retained.LegalHold, deletedAt);
                    if (removed.Count == 0) return null;
                    if (removed.Any(v => !tokens.ContainsKey(v.Blob)))
                    {
                        unwritten = true;
                        return null;
                    }
                    return head with
                    {
                        Versions = [.. head.Versions.Except(removed)],
                        Deleted = [.. head.Deleted ?? [], .. removed.Select(v => v with { DeletedAt = deletedAt, PurgeAt = purgeAt, BinToken = tokens[v.Blob] })]
                    };
                }, cancellationToken, withBin: true).ConfigureAwait(false);
                if (!unwritten) break;
            }
        }
        catch
        {
            await DeleteQuietlyAsync([.. written]).ConfigureAwait(false);
            throw;
        }

        HashSet<string> kept = changed
            ? [.. removed.SelectMany(v => BinKeysOf(container.Id, v with { DeletedAt = deletedAt, PurgeAt = purgeAt, BinToken = tokens[v.Blob] }))]
            : [];
        await DeleteQuietlyAsync([.. written.Where(key => !kept.Contains(key)),
            .. removed.Select(v => ExpiryKeyOf(container.Id, v)).OfType<string>().Where(_ => changed)]).ConfigureAwait(false);
        return changed;
    }

    /// <summary>The keys of a version's bin entry and purge entry, for a version in a bin.</summary>
    private IEnumerable<string> BinKeysOf(string containerId, StoredVersion version)
    {
        yield return _keys.BinEntry(containerId, version.DeletedAt!.Value, version.VersionId, version.BinToken!, version.ContentLength);
        yield return _keys.Purge(version.PurgeAt!.Value, version.VersionId, version.BinToken!, containerId, version.Blob);
    }

    /// <summary>
    /// Lists the container's bin entries from where the last page ended, reading each one's head to check it, and stops
    /// once the page is full or, with a prefix, once it has looked at <see cref="MaxBinEntriesExamined"/> entries.
    /// </summary>
    public override async Task<DeletedVersionPage> GetDeletedPageAsync(string resourceContainerName, string? prefix,
        ResourceVersionKey? after, int pageSize, CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is not { } container)
            return new DeletedVersionPage([], null);

        string bin = _keys.Bin(container.Id);
        string? startAfter = after is { } last ? _keys.BinFrom(container.Id, last.At, last.VersionId) : null;
        var page = new List<ResourceInfo>(pageSize);
        var batch = new List<ObjectListing>();
        int examined = 0;
        // The time and version ID of the first entry past where it stopped, which says another page follows.
        (DateTimeOffset At, string VersionId)? next = null;
        (DateTimeOffset, string)? group = null;
        await foreach (ObjectListing entry in store.ListAsync(bin, startAfter, cancellationToken).ConfigureAwait(false))
        {
            (DateTimeOffset deletedAt, string versionId, _, _) = ObjectKeys.ParseBinEntry(entry.Key[bin.Length..]);
            // Entries with the same time and version ID sort by name only once they're read, so the listing never stops
            // inside such a group.
            if ((page.Count >= pageSize || examined >= MaxBinEntriesExamined) && group != (deletedAt, versionId))
            {
                next = (deletedAt, versionId);
                break;
            }
            group = (deletedAt, versionId);
            batch.Add(entry);
            if (batch.Count < Math.Min(pageSize - page.Count, ParallelReads)) continue;
            examined += batch.Count;
            page.AddRange((await ReadBinnedAsync(resourceContainerName, container.Id, bin, batch, prefix, cancellationToken).ConfigureAwait(false))
                .Where(v => IsPast(v, v.DeletedAt, after)));
            batch.Clear();
        }
        if (batch.Count > 0)
        {
            page.AddRange((await ReadBinnedAsync(resourceContainerName, container.Id, bin, batch, prefix, cancellationToken).ConfigureAwait(false))
                .Where(v => IsPast(v, v.DeletedAt, after)));
        }
        SortGroupsByName(page, v => v.DeletedAt);

        // A full page continues after its last version. One that stopped looking continues at the start of the next
        // entry's group: the key's name is empty, which sorts before every real name, so it excludes nothing in that group.
        if (page.Count > pageSize || (page.Count == pageSize && next is not null))
        {
            page.RemoveRange(pageSize, page.Count - pageSize);
            return new DeletedVersionPage(page, new ResourceVersionKey(page[^1].DeletedAt!.Value,
                new ResourceAddress(resourceContainerName, page[^1].Name), page[^1].VersionId));
        }
        return new DeletedVersionPage(page, next is { } start
            ? new ResourceVersionKey(start.At, new ResourceAddress(resourceContainerName, ""), start.VersionId)
            : null);
    }

    /// <summary>
    /// The versions a batch of bin entries name, in the entries' order, leaving out those of names outside
    /// <paramref name="prefix"/> and stale entries, which are dropped once old enough.
    /// </summary>
    private async Task<IEnumerable<ResourceInfo>> ReadBinnedAsync(string containerName, string containerId, string bin,
        List<ObjectListing> entries, string? prefix, CancellationToken cancellationToken)
    {
        var found = new ResourceInfo?[entries.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                if ((await GetAsync<ExpiryEntry>(entries[i].Key, ct).ConfigureAwait(false)).Value is not { } named) return;
                if (prefix is not null && !named.ResourceName.StartsWith(prefix, StringComparison.Ordinal)) return;
                (_, string versionId, string token, _) = ObjectKeys.ParseBinEntry(entries[i].Key[bin.Length..]);
                Head? head = await ReadStoredHeadAsync(_keys.Head(containerId, named.ResourceName), ct).ConfigureAwait(false);
                if (head?.Deleted?.FirstOrDefault(v => v.BinToken == token && v.VersionId == versionId) is { } version)
                    found[i] = ToInfo(containerName, named.ResourceName, version, false);
                else
                    await DropIfStaleAsync(entries[i]).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return found.OfType<ResourceInfo>();
    }

    /// <summary>Deletes a bin or purge entry whose version isn't in its bin, once it's too old to belong to a write in progress.</summary>
    private async Task DropIfStaleAsync(ObjectListing entry)
    {
        if (entry.LastModified >= RealTime.GetUtcNow() - StaleExpiryAge) return;
        await DeleteQuietlyAsync([entry.Key]).ConfigureAwait(false);
        ObjectStoreTelemetry.CountStaleBinEntry(storeName);
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetDeletedVersionsAsync(ResourceAddress resource,
        CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return [];
        Head? head = await ReadStoredHeadAsync(_keys.Head(container.Id, resource.ResourceName), cancellationToken).ConfigureAwait(false);
        return [.. (head?.Deleted ?? [])
            .OrderByDescending(v => v.DeletedAt)
            .ThenBy(v => v.VersionId, StringComparer.Ordinal)
            .Select(v => ToInfo(resource.ContainerName, resource.ResourceName, v, false))];
    }

    /// <summary>
    /// Writes an expiry entry for each version coming back that expires, then moves them out of the bin in one head
    /// write, and deletes their bin and purge entries. When another writer changes the head so a version comes back whose
    /// expiry entry wasn't written, it starts again.
    /// </summary>
    public override async Task<long> UndeleteAsync(ResourceAddress resource, string? versionId, CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return 0;
        string headKey = _keys.Head(container.Id, resource.ResourceName);
        bool Returning(StoredVersion version) => versionId is null || version.VersionId == versionId;

        // The tokens of the expiry entries written, by blob.
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var written = new List<string>();
        var back = new List<StoredVersion>();
        bool changed;
        try
        {
            while (true)
            {
                Head? read = await ReadStoredHeadAsync(headKey, cancellationToken).ConfigureAwait(false);
                foreach (StoredVersion version in read?.Deleted?.Where(v => Returning(v) && v.ExpiresAt is not null && !tokens.ContainsKey(v.Blob)) ?? [])
                {
                    string token = Guid.NewGuid().ToString("N");
                    string key = _keys.Expiry(version.ExpiresAt!.Value, version.VersionId, token, container.Id, version.Blob);
                    await PutEntryAsync(key, resource, cancellationToken).ConfigureAwait(false);
                    written.Add(key);
                    tokens[version.Blob] = token;
                }

                bool unwritten = false;
                changed = await ChangeHeadKeepingSearchAsync(container.Id, resource.ResourceName, head =>
                {
                    unwritten = false;
                    back.Clear();
                    back.AddRange(head?.Deleted?.Where(Returning) ?? []);
                    if (back.Count == 0) return null;
                    if (back.Any(v => v.ExpiresAt is not null && !tokens.ContainsKey(v.Blob)))
                    {
                        unwritten = true;
                        return null;
                    }
                    List<StoredVersion> left = [.. head!.Deleted!.Except(back)];
                    return head with
                    {
                        Versions = WithOlder(head.Versions, back.Select(v => v with
                        {
                            DeletedAt = null, PurgeAt = null, BinToken = null,
                            ExpiryToken = v.ExpiresAt is null ? null : tokens[v.Blob]
                        })),
                        Deleted = left.Count == 0 ? null : left
                    };
                }, cancellationToken, withBin: true).ConfigureAwait(false);
                if (!unwritten) break;
            }
        }
        catch
        {
            await DeleteQuietlyAsync([.. written]).ConfigureAwait(false);
            throw;
        }

        if (!changed)
        {
            await DeleteQuietlyAsync([.. written]).ConfigureAwait(false);
            return 0;
        }
        HashSet<string> kept = [.. back.Where(v => v.ExpiresAt is not null)
            .Select(v => _keys.Expiry(v.ExpiresAt!.Value, v.VersionId, tokens[v.Blob], container.Id, v.Blob))];
        await DeleteQuietlyAsync([.. written.Where(key => !kept.Contains(key)), .. back.SelectMany(v => BinKeysOf(container.Id, v))])
            .ConfigureAwait(false);
        return back.Count;
    }

    /// <summary>
    /// A name's versions with <paramref name="returning"/> among the older ones, each placed after every older version
    /// stored no later than it, so they keep the order they were first stored in; the current version stays last. A
    /// name with no current version gets the most recently stored of them as current.
    /// </summary>
    private static List<StoredVersion> WithOlder(List<StoredVersion> versions, IEnumerable<StoredVersion> returning)
    {
        List<StoredVersion> older = versions.Count == 0 ? [] : versions[..^1];
        foreach (StoredVersion version in returning.OrderBy(v => v.LastModified))
        {
            int later = older.FindIndex(v => v.LastModified > version.LastModified);
            older.Insert(later < 0 ? older.Count : later, version);
        }
        return versions.Count == 0 ? older : [.. older, versions[^1]];
    }

    /// <summary>
    /// Takes the versions out of the bin in one head write, then deletes their content and every entry they have.
    /// Content whose delete fails is no longer named by any head, and stays until the sweep deletes it.
    /// </summary>
    public override async Task<long> PurgeDeletedAsync(ResourceAddress resource, string? versionId, DateTimeOffset? dueBy,
        CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return 0;
        var purged = new List<StoredVersion>();
        bool changed = await ChangeStoredHeadAsync(_keys.Head(container.Id, resource.ResourceName), head =>
        {
            purged.Clear();
            purged.AddRange(head?.Deleted?.Where(v => (versionId is null || v.VersionId == versionId) && !(v.PurgeAt > dueBy)) ?? []);
            if (purged.Count == 0) return null;
            List<StoredVersion> left = [.. head!.Deleted!.Except(purged)];
            return head with { Deleted = left.Count == 0 ? null : left };
        }, cancellationToken).ConfigureAwait(false);
        if (!changed) return 0;

        await DeleteQuietlyAsync([.. purged.SelectMany(v => KeysOf(container.Id, v).Concat(BinKeysOf(container.Id, v)))]).ConfigureAwait(false);
        return purged.Count;
    }

    /// <summary>Lists the purge entries due by <paramref name="now"/>, as <see cref="GetExpiredAsync"/> lists expiry entries.</summary>
    public override Task<IReadOnlyList<ResourceInfo>> GetPurgeableAsync(DateTimeOffset now, int maxVersions,
        ResourceVersionKey? after = null, CancellationToken cancellationToken = default) =>
        ListDueAsync(_keys.Purges, after is { } last ? _keys.PurgesFrom(last.At, last.VersionId) : null, after, now, maxVersions,
            v => v.PurgeAt, ReadPurgeableAsync, cancellationToken);

    /// <summary>The versions a batch of purge entries name, in the entries' order, leaving out stale entries.</summary>
    private async Task<IEnumerable<ResourceInfo>> ReadPurgeableAsync(List<ObjectListing> entries, CancellationToken cancellationToken)
    {
        var versions = new ResourceInfo?[entries.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                (_, string versionId, string token, string containerId, string blob) = ObjectKeys.ParseExpiry(entries[i].Key[_keys.Purges.Length..]);
                if ((await GetAsync<ExpiryEntry>(entries[i].Key, ct).ConfigureAwait(false)).Value is not { } named) return;
                ContainerRecord? container = await GetContainerAsync(named.ContainerName, ct).ConfigureAwait(false);
                Head? head = container?.Id == containerId
                    ? await ReadStoredHeadAsync(_keys.Head(containerId, named.ResourceName), ct).ConfigureAwait(false)
                    : null;
                if (head?.Deleted?.FirstOrDefault(v => v.Blob == blob && v.BinToken == token && v.VersionId == versionId) is { } version)
                    versions[i] = ToInfo(named.ContainerName, named.ResourceName, version, false);
                else
                    await DropIfStaleAsync(entries[i]).ConfigureAwait(false);
            }).ConfigureAwait(false);
        return versions.OfType<ResourceInfo>();
    }
}
