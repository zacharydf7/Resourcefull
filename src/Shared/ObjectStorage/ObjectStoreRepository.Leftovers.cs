using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resourcefull.ObjectStorage;

// Leftovers: a write uploads a version's content before the head write that names it, so a write that stops between
// the two leaves content no head names, which costs storage and counts toward its container's usage. A delete whose
// content delete fails leaves the same. A scan for such content reads every head, so it runs at most once a day, and
// content is deleted only when two scans in a row found it unnamed: a single scan reads the heads one after another,
// so a version moved from a name it hadn't read yet to one it had already read would look unnamed to it. Since it reads
// every head, it also drops bin entries no head's bin has, and fixes the marks of names left with only a bin, which the
// usage figures trust without checking.
internal sealed partial class ObjectStoreRepository
{
    /// <summary>How long after one scan for content no head names the next one may run.</summary>
    private static readonly TimeSpan ContentScanInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Deletes content that both this scan and the one before found named by no head, and that's older than
    /// <paramref name="staleBefore"/>, if a day has passed since the last scan; and finishes purging deleted containers.
    /// If a container can't be scanned or purged, its exception is added to <paramref name="failures"/> and the scan goes
    /// on to the next.
    /// </summary>
    /// <returns>How many content objects it deleted.</returns>
    private async Task<long> SweepUnnamedContentAsync(DateTimeOffset staleBefore, List<Exception> failures,
        CancellationToken cancellationToken)
    {
        Stored<ContentScan> read = await GetAsync<ContentScan>(_keys.Scan, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = RealTime.GetUtcNow();
        if (read.Value is { } last && last.ScannedAt > now - ContentScanInterval) return 0;
        // Claimed first, so another instance sweeping meanwhile skips the scan.
        var claimed = new ContentScan(now, read.Value?.Unnamed ?? []);
        if (!await TryPutAsync(_keys.Scan, id => Serialize(claimed with { WriteId = id }), read.ETag).ConfigureAwait(false)) return 0;

        // Content is stamped by the store, in real time, while the cutoff is on the clock versions are stamped with.
        DateTimeOffset cutoff = now - (_clock.GetUtcNow() - staleBefore);
        HashSet<string> moving = await ReadKeptKeysAsync(cancellationToken).ConfigureAwait(false);
        var unnamed = new HashSet<string>(StringComparer.Ordinal);
        await foreach (string[] keys in ListKeysAsync(_keys.Containers, null, _ => true, () => ParallelReads, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach ((_, ContainerRecord? record) in await ReadAllAsync<ContainerRecord>(keys, cancellationToken).ConfigureAwait(false))
            {
                if (record is null) continue;
                try
                {
                    if (record.Deleted) await PurgeAsync(record.Id, cancellationToken).ConfigureAwait(false);
                    else unnamed.UnionWith(await FindUnnamedContentAsync(record.Id, cutoff, moving, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failures.Add(exception);
                }
            }
        }

        string[] deleting = [.. claimed.Unnamed.Where(unnamed.Contains)];
        await store.DeleteAsync(deleting, cancellationToken).ConfigureAwait(false);
        unnamed.ExceptWith(deleting);
        // No ETag check: the claim already keeps every other scan away for a day.
        await store.UploadAsync(_keys.Scan, new MemoryStream(Serialize(new ContentScan(now, [.. unnamed]))), cancellationToken)
            .ConfigureAwait(false);
        return deleting.Length;
    }

    /// <summary>
    /// The keys every move lists, and every upload going up in pieces. A move's copies are named by no head until it
    /// marks the destination, and an upload's pieces until they're joined, so they're kept. A record of pieces that's no
    /// longer written again belongs to an upload that stopped; its pieces aren't kept, and the record is deleted.
    /// </summary>
    private async Task<HashSet<string>> ReadKeptKeysAsync(CancellationToken cancellationToken)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        await foreach (string[] keys in ListKeysAsync(_keys.Moves, null, _ => true, () => ParallelReads, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach ((_, MoveEntry? entry) in await ReadAllAsync<MoveEntry>(keys, cancellationToken).ConfigureAwait(false))
                kept.UnionWith([.. entry?.SourceKeys ?? [], .. entry?.DestinationKeys ?? []]);
        }
        DateTimeOffset staleBefore = RealTime.GetUtcNow() - StalePiecesAge;
        var young = new List<string>();
        var stale = new List<string>();
        await foreach (ObjectListing item in store.ListAsync(_keys.Pieces, null, cancellationToken).ConfigureAwait(false))
            (item.LastModified >= staleBefore ? young : stale).Add(item.Key);
        foreach ((_, PiecesRecord? record) in await ReadAllAsync<PiecesRecord>([.. young], cancellationToken).ConfigureAwait(false))
            kept.UnionWith(record?.Keys ?? []);
        await DeleteQuietlyAsync([.. stale]).ConfigureAwait(false);
        return kept;
    }

    /// <summary>
    /// The keys of a container's content objects older than <paramref name="cutoff"/> that none of its heads names, in its
    /// versions or its bin, and that aren't in <paramref name="kept"/>. From the same reading of the heads, it also
    /// corrects the container's bin index (see <see cref="SettleBinIndexAsync"/>).
    /// </summary>
    private async Task<List<string>> FindUnnamedContentAsync(string containerId, DateTimeOffset cutoff, HashSet<string> kept,
        CancellationToken cancellationToken)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        var binTokens = new HashSet<string>(StringComparer.Ordinal);
        var emptied = new HashSet<string>(StringComparer.Ordinal);
        var moving = new HashSet<string>(StringComparer.Ordinal);
        await foreach (string[] heads in ListKeysAsync(_keys.Heads(containerId), null, item => item.Size > EmptyHeadSize,
            () => ParallelReads * 4, cancellationToken).ConfigureAwait(false))
        {
            foreach ((string key, Head? head) in await ReadAllAsync<Head>(heads, cancellationToken).ConfigureAwait(false))
            {
                named.UnionWith(head?.Versions.Concat(head.Deleted ?? []).Select(v => _keys.Content(containerId, v.Blob)) ?? []);
                binTokens.UnionWith(head?.Deleted?.Select(v => v.BinToken!) ?? []);
                // A head a move has marked may or may not end up with only a bin until the move is decided, so its mark
                // is left alone.
                if (head?.Move is not null) moving.Add(_keys.EmptiedFor(key));
                else if (IsBinOnly(head)) emptied.Add(_keys.EmptiedFor(key));
            }
        }
        await SettleBinIndexAsync(containerId, cutoff, binTokens, emptied, moving, cancellationToken).ConfigureAwait(false);
        var unnamed = new List<string>();
        await foreach (ObjectListing content in store.ListAsync(_keys.Contents(containerId), null, cancellationToken).ConfigureAwait(false))
        {
            if (content.LastModified < cutoff && !named.Contains(content.Key) && !kept.Contains(content.Key)) unnamed.Add(content.Key);
        }
        return unnamed;
    }

    /// <summary>
    /// Deletes the container's bin entries and bin-only marks older than <paramref name="cutoff"/> that no head accounts
    /// for: an entry whose token no head's bin has, or a mark beside a head that isn't left with only a bin and isn't part
    /// of a move. Then writes the marks missing beside heads left with only a bin. A head a mark is put or deleted for is
    /// read again first, since a write may have changed it since the scan read it. The usage figures read both from
    /// listings, so this keeps them from staying wrong after a write that failed partway.
    /// </summary>
    private async Task SettleBinIndexAsync(string containerId, DateTimeOffset cutoff, HashSet<string> binTokens,
        HashSet<string> emptied, HashSet<string> moving, CancellationToken cancellationToken)
    {
        string bin = _keys.Bin(containerId);
        var stale = new List<string>();
        await foreach (ObjectListing entry in store.ListAsync(bin, null, cancellationToken).ConfigureAwait(false))
        {
            if (entry.LastModified < cutoff && !binTokens.Contains(ObjectKeys.ParseBinEntry(entry.Key[bin.Length..]).Token))
                stale.Add(entry.Key);
        }
        foreach (string[] batch in stale.Chunk(PurgeBatchSize)) await store.DeleteAsync(batch, cancellationToken).ConfigureAwait(false);
        ObjectStoreTelemetry.CountStaleBinEntries(stale.Count, storeName);

        var marked = new HashSet<string>(StringComparer.Ordinal);
        var unmarking = new List<string>();
        await foreach (ObjectListing mark in store.ListAsync(_keys.EmptiedNames(containerId), null, cancellationToken).ConfigureAwait(false))
        {
            marked.Add(mark.Key);
            if (mark.LastModified < cutoff && !emptied.Contains(mark.Key) && !moving.Contains(mark.Key)) unmarking.Add(mark.Key);
        }
        foreach (string mark in unmarking.Concat(emptied.Except(marked)))
        {
            bool binOnly = IsBinOnly(await ReadStoredHeadAsync(_keys.HeadForEmptied(mark), cancellationToken).ConfigureAwait(false));
            if (binOnly && !marked.Contains(mark)) await store.UploadAsync(mark, new MemoryStream([]), cancellationToken).ConfigureAwait(false);
            else if (!binOnly && marked.Contains(mark)) await store.DeleteAsync([mark], cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] Serialize(ContentScan scan) => JsonSerializer.SerializeToUtf8Bytes(scan);

    /// <summary>When the last scan for unnamed content ran, and the content it found unnamed, for the next to check.</summary>
    private sealed record ContentScan(
        [property: JsonPropertyName("at")] DateTimeOffset ScannedAt,
        [property: JsonPropertyName("u")] List<string> Unnamed,
        [property: JsonPropertyName("w")] string? WriteId = null);
}
