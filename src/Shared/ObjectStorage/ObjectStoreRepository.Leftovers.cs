using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resourcefull.ObjectStorage;

// Leftovers: a write uploads a version's content before the head write that names it, so a write that stops between
// the two leaves content no head names, which costs storage and counts toward its container's usage. A delete whose
// content delete fails leaves the same. A scan for such content reads every head, so it runs at most once a day, and
// content is deleted only when two scans in a row found it unnamed: a single scan reads the heads one after another,
// so a version moved from a name it hadn't read yet to one it had already read would look unnamed to it.
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
    private async Task<int> SweepUnnamedContentAsync(DateTimeOffset staleBefore, List<Exception> failures,
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
    /// The keys of a container's content objects older than <paramref name="cutoff"/> that none of its heads names and no
    /// move lists.
    /// </summary>
    private async Task<List<string>> FindUnnamedContentAsync(string containerId, DateTimeOffset cutoff, HashSet<string> moving,
        CancellationToken cancellationToken)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        await foreach (string[] heads in ListKeysAsync(_keys.Heads(containerId), null, item => item.Size > EmptyHeadSize,
            () => ParallelReads * 4, cancellationToken).ConfigureAwait(false))
        {
            foreach ((_, Head? head) in await ReadAllAsync<Head>(heads, cancellationToken).ConfigureAwait(false))
                named.UnionWith(head?.Versions.Select(v => _keys.Content(containerId, v.Blob)) ?? []);
        }
        var unnamed = new List<string>();
        await foreach (ObjectListing content in store.ListAsync(_keys.Contents(containerId), null, cancellationToken).ConfigureAwait(false))
        {
            if (content.LastModified < cutoff && !named.Contains(content.Key) && !moving.Contains(content.Key)) unnamed.Add(content.Key);
        }
        return unnamed;
    }

    private static byte[] Serialize(ContentScan scan) => JsonSerializer.SerializeToUtf8Bytes(scan);

    /// <summary>When the last scan for unnamed content ran, and the content it found unnamed, for the next to check.</summary>
    private sealed record ContentScan(
        [property: JsonPropertyName("at")] DateTimeOffset ScannedAt,
        [property: JsonPropertyName("u")] List<string> Unnamed,
        [property: JsonPropertyName("w")] string? WriteId = null);
}
