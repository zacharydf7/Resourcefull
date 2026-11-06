using System.Diagnostics;
using Resourcefull.Providers;

namespace Resourcefull.ObjectStorage;

// Metadata search: a name's current version has an empty search entry for each of its searchable keys' values, at
// d/{container}/m/{token of the key and value}/{escaped name}!, written before the head that makes those values current.
// A search lists the entries under its token, which sort by name, and reads each named head, keeping only the names
// whose current version still has the value. An entry the head no longer agrees with is skipped, and deleted once it's
// old enough to belong to no write still in progress. Writers to one name share its entries, so every write puts its
// entries again once its head change lands, and whatever deletes an entry reads the head again afterward and puts back
// one the current version needs: either way, the version current at the end has its entries.
internal sealed partial class ObjectStoreRepository
{
    public override async Task<IReadOnlyList<ResourceInfo>> FindResourcePageAsync(string containerName, string? prefix,
        string? afterName, int pageSize, ResourceMetadataMatch match, CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(containerName, cancellationToken).ConfigureAwait(false) is not { } container)
            return [];

        string entries = _keys.SearchEntries(container.Id, match.Key, match.Value);
        string? startAfter = afterName is null ? null : _keys.SearchEntry(container.Id, match.Key, match.Value, afterName);
        var page = new List<ResourceInfo>(pageSize);
        var batch = new List<ObjectListing>();
        int examined = 0;
        await foreach (ObjectListing entry in store.ListAsync(entries + (prefix is null ? "" : _keys.Escape(prefix)), startAfter,
            cancellationToken).ConfigureAwait(false))
        {
            batch.Add(entry);
            if (batch.Count < Math.Min(pageSize - page.Count, ParallelReads)) continue;
            examined += batch.Count;
            page.AddRange(await ReadMatchesAsync(containerName, container.Id, entries, batch, match, cancellationToken).ConfigureAwait(false));
            batch.Clear();
            if (page.Count >= pageSize) break;
        }
        examined += batch.Count;
        page.AddRange(await ReadMatchesAsync(containerName, container.Id, entries, batch, match, cancellationToken).ConfigureAwait(false));
        // On the client's span only, never an application's own.
        if (Activity.Current is { } activity && activity.Source.Name == ResourcefullTelemetry.ActivitySourceName)
            activity.SetTag("resourcefull.search_examined", examined);
        return page;
    }

    /// <summary>
    /// The current versions a batch of search entries name, in the entries' order, if they still have the value; stale
    /// entries are left out, and dropped once old enough.
    /// </summary>
    private async Task<IEnumerable<ResourceInfo>> ReadMatchesAsync(string containerName, string containerId, string entries,
        List<ObjectListing> batch, ResourceMetadataMatch match, CancellationToken cancellationToken)
    {
        var found = new ResourceInfo?[batch.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, batch.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                string name = ObjectKeys.Unescape(batch[i].Key[entries.Length..]);
                Head? head = await ReadHeadAsync(_keys.Head(containerId, name), ct).ConfigureAwait(false);
                if (head?.Versions[^1] is { } current && match.IsMatch(current.Metadata))
                {
                    found[i] = ToInfo(containerName, name, current, true);
                }
                else if (batch[i].LastModified < RealTime.GetUtcNow() - StaleExpiryAge)
                {
                    await DropSearchEntriesAsync(containerId, name, [batch[i].Key]).ConfigureAwait(false);
                    ObjectStoreTelemetry.CountStaleSearchEntry(storeName);
                }
            }).ConfigureAwait(false);
        return found.OfType<ResourceInfo>();
    }

    public override async Task<long> IndexMetadataAsync(string containerName, CancellationToken cancellationToken = default)
    {
        if (SearchableMetadataKeys.Count == 0
            || await GetContainerAsync(containerName, cancellationToken).ConfigureAwait(false) is not { } container)
        {
            return 0;
        }

        string heads = _keys.Heads(container.Id);
        long indexed = 0;
        await foreach (string[] keys in ListKeysAsync(heads, null, item => item.Size > EmptyHeadSize, () => int.MaxValue,
            cancellationToken).ConfigureAwait(false))
        {
            foreach ((string key, Head? read) in await ReadAllAsync<Head>(keys, cancellationToken).ConfigureAwait(false))
            {
                Head? head = read?.Move is null ? Live(read) : await ReadHeadAsync(key, cancellationToken).ConfigureAwait(false);
                if (head is null) continue;
                await PutSearchEntriesAsync(SearchKeysOf(container.Id, ObjectKeys.Unescape(key[heads.Length..]), head.Versions[^1]),
                    cancellationToken).ConfigureAwait(false);
                indexed++;
            }
        }
        return indexed;
    }

    /// <summary>
    /// Changes a name's head as <see cref="ChangeHeadAsync"/> does, first writing the search entries of the version the
    /// change leaves current, when that's another version or the same one with other values: a dry run of
    /// <paramref name="change"/> on the head as it reads now says which. If another writer then changes the head so the
    /// change leaves a version current whose entries weren't written, it starts again. Afterward, the entries only the
    /// version current before needed are deleted. <paramref name="change"/> may be called more than once, as for
    /// <see cref="ChangeHeadAsync"/>; with <paramref name="withBin"/>, it gets the head with its bin, as for
    /// <see cref="ChangeStoredHeadAsync"/>.
    /// </summary>
    private async Task<bool> ChangeHeadKeepingSearchAsync(string containerId, string resourceName, Func<Head?, Head?> change,
        CancellationToken cancellationToken, bool withBin = false)
    {
        string headKey = _keys.Head(containerId, resourceName);
        Task<bool> ChangeAsync(Func<Head?, Head?> apply) =>
            withBin ? ChangeStoredHeadAsync(headKey, apply, cancellationToken) : ChangeHeadAsync(headKey, apply, cancellationToken);
        if (SearchableMetadataKeys.Count == 0) return await ChangeAsync(change).ConfigureAwait(false);
        while (true)
        {
            Head? read = withBin
                ? await ReadStoredHeadAsync(headKey, cancellationToken).ConfigureAwait(false)
                : await ReadHeadAsync(headKey, cancellationToken).ConfigureAwait(false);
            HashSet<string> written = EntriesToWrite(containerId, resourceName, read, read is null ? null : change(read));
            await PutSearchEntriesAsync(written, cancellationToken).ConfigureAwait(false);

            bool unwritten = false;
            List<string> dropped = [];
            bool changed = await ChangeAsync(head =>
            {
                Head? next = change(head);
                if (next is null) return null;
                if (!EntriesToWrite(containerId, resourceName, head, next).IsSubsetOf(written))
                {
                    unwritten = true;
                    return null;
                }
                dropped = [.. SearchKeysOf(containerId, resourceName, head?.Versions.LastOrDefault())
                    .Except(SearchKeysOf(containerId, resourceName, next.Versions.LastOrDefault()))];
                return next;
            }).ConfigureAwait(false);
            if (unwritten) continue;
            if (changed) await SettleSearchEntriesAsync(containerId, resourceName, written, dropped).ConfigureAwait(false);
            return changed;
        }
    }

    /// <summary>
    /// The search entries a head change from <paramref name="before"/> to <paramref name="after"/> needs written first:
    /// those of the version it leaves current, unless that's the version current before, with the same values.
    /// </summary>
    private HashSet<string> EntriesToWrite(string containerId, string resourceName, Head? before, Head? after)
    {
        StoredVersion? current = after?.Versions.LastOrDefault();
        HashSet<string> needed = SearchKeysOf(containerId, resourceName, current);
        StoredVersion? previous = before?.Versions.LastOrDefault();
        return previous?.VersionId == current?.VersionId && needed.SetEquals(SearchKeysOf(containerId, resourceName, previous))
            ? []
            : needed;
    }

    /// <summary>
    /// Once a write's head change has landed, puts its entries again, in case another write to the name deleted them
    /// meanwhile, then drops the entries only the version it replaced needed. It can't be canceled: the write has
    /// succeeded, and the version current at the end must have its entries.
    /// </summary>
    private async Task SettleSearchEntriesAsync(string containerId, string resourceName, IEnumerable<string> written,
        List<string> dropped)
    {
        await PutSearchEntriesAsync(written, CancellationToken.None).ConfigureAwait(false);
        if (dropped.Count > 0) await DropSearchEntriesAsync(containerId, resourceName, dropped).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes search entries of a name, then reads its head again and puts back any of them its current version needs:
    /// another write may have made it current between the head read that judged them stale and the delete. It can't be
    /// canceled, so an entry it deletes is always put back when needed.
    /// </summary>
    private async Task DropSearchEntriesAsync(string containerId, string resourceName, List<string> keys)
    {
        await DeleteQuietlyAsync([.. keys]).ConfigureAwait(false);
        Head? head = await ReadHeadAsync(_keys.Head(containerId, resourceName), CancellationToken.None).ConfigureAwait(false);
        await PutSearchEntriesAsync(SearchKeysOf(containerId, resourceName, head?.Versions[^1]).Intersect(keys), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>The keys of the search entries <paramref name="version"/> needs as its name's current version.</summary>
    private HashSet<string> SearchKeysOf(string containerId, string resourceName, StoredVersion? version) =>
        version is null ? [] : SearchKeysOf(containerId, resourceName, version.Metadata);

    // A value too long for a search to look for, stored before its key was named, gets no entry, as with the databases.
    private HashSet<string> SearchKeysOf(string containerId, string resourceName, IReadOnlyDictionary<string, string> metadata) =>
        [.. SearchableMetadataKeys
            .Where(key => metadata.TryGetValue(key, out string? value) && value.Length <= ResourceMetadataMatch.MaxValueLength)
            .Select(key => _keys.SearchEntry(containerId, key, metadata[key], resourceName))];

    /// <summary>Writes empty search entries at <paramref name="keys"/>, several at once.</summary>
    private Task PutSearchEntriesAsync(IEnumerable<string> keys, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (key, ct) => await store.UploadAsync(key, new MemoryStream([]), ct).ConfigureAwait(false));
}
