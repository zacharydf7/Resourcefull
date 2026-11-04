using System.Text.Json;
using System.Text.Json.Serialization;
using Resourcefull.Providers;

namespace Resourcefull.ObjectStorage;

// Moves: a move takes every version of a name at once, and the store changes one object per write. So a move marks
// both heads with its ID, and its entry under {prefix}m/ decides it: the move happens with the one conditional write
// that marks the entry committed. Until then, readers see the versions at the source; from then, at the destination.
// A writer that finds a head marked by a move still pending abandons the move first, by marking its entry abandoned,
// so the mover's commit is refused. Once a move is decided, the mover clears both marks and deletes the entry; the
// sweep finishes a move whose mover stopped first.
internal sealed partial class ObjectStoreRepository
{
    /// <summary>
    /// How old a move's entry must be before the sweep finishes the move: a younger one may belong to a move still
    /// being made.
    /// </summary>
    private static readonly TimeSpan StaleMoveAge = TimeSpan.FromHours(1);

    /// <summary>
    /// How much of a version's content one part of a copy across containers takes. Tests set it lower, though not below
    /// <see cref="ObjectParts.PartSize"/>.
    /// </summary>
    internal long CopyPartSize { get; init; } = ObjectParts.MaxJoinedPartSize;

    /// <summary>
    /// Writes the move's entry, copies the versions' content to the destination container when it's another one, marks
    /// the source head and writes the destination's, each only if unchanged since it was read, then commits the entry.
    /// When another writer changed either head first, the try is undone and the move tried again.
    /// </summary>
    public override async Task<bool> MoveResourceAsync(ResourceLocation source, ResourceLocation destination,
        ResourceConditions? sourceConditions, CancellationToken cancellationToken = default)
    {
        ContainerRecord to = await GetContainerAsync(destination.ContainerName, cancellationToken).ConfigureAwait(false)
            ?? throw ContainerMissing(destination.ContainerName);
        if (await GetContainerAsync(source.ContainerName, cancellationToken).ConfigureAwait(false) is not { } from)
        {
            sourceConditions?.ThrowIfNotMet(source, null, null);
            return false;
        }

        string sourceKey = _keys.Head(from.Id, source.ResourceName);
        string destinationKey = _keys.Head(to.Id, destination.ResourceName);
        bool across = from.Id != to.Id;
        bool protects;
        for (int attempt = 1; ; attempt++)
        {
            (Stored<Head> sourceRead, Head? moving) = await ReadHeadToChangeAsync(sourceKey, cancellationToken).ConfigureAwait(false);
            StoredVersion? current = moving?.Versions[^1];
            sourceConditions?.ThrowIfNotMet(source, current?.VersionId, current?.ContentHash);
            if (moving is null) return false;
            (Stored<Head> destinationRead, Head? existing) = await ReadHeadToChangeAsync(destinationKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null) throw ResourceExists(destination);

            // Across containers, each version's content is copied under the destination's ID, so the destination's
            // usage counts it and deleting the source container doesn't take it. Every version gets a new expiry entry
            // and protection entry for its new name.
            List<StoredVersion> moved = [.. moving.Versions.Select(v => v with
            {
                Blob = across ? Guid.NewGuid().ToString("N") : v.Blob,
                ExpiryToken = v.ExpiresAt is null ? null : Guid.NewGuid().ToString("N"),
                ProtectionToken = v.ProtectionToken is null ? null : Guid.NewGuid().ToString("N")
            })];
            protects = moved.Any(v => v.ProtectionToken is not null);
            var entry = new MoveEntry(MoveState.Pending, from.Id, source.ResourceName, to.Id, destination.ResourceName,
                [.. moving.Versions.SelectMany(v => LeftBehind(from.Id, v, across))],
                [.. moved.SelectMany(v => LeftBehind(to.Id, v, across))]);
            if (await TryMoveAsync(entry, moving, moved, sourceRead.ETag, destinationRead.ETag, destination, cancellationToken)
                .ConfigureAwait(false))
            {
                break;
            }

            ObjectStoreTelemetry.CountRetry(storeName);
            await WaitToRetryAsync(sourceKey, attempt, cancellationToken).ConfigureAwait(false);
        }

        // As for an upload: a destination container deleted meanwhile took the versions with it.
        if (protects) await WaitForContainerDeleteAsync(to, destination.ContainerName).ConfigureAwait(false);
        else if ((await GetContainerAsync(destination.ContainerName, CancellationToken.None).ConfigureAwait(false))?.Id != to.Id)
        {
            await PurgeQuietlyAsync(to.Id).ConfigureAwait(false);
            throw ContainerMissing(destination.ContainerName);
        }
        return true;
    }

    /// <summary>
    /// What a version leaves on the side of a move it doesn't end up on: across containers, its content and its expiry
    /// and protection entries; within one, just its entries, since the content stays where it is.
    /// </summary>
    private IEnumerable<string> LeftBehind(string containerId, StoredVersion version, bool across) =>
        across ? KeysOf(containerId, version) : new[] { ExpiryKeyOf(containerId, version), ProtectionKeyOf(containerId, version) }.OfType<string>();

    /// <summary>Makes one try at a move, and finishes it, whichever way it was decided.</summary>
    /// <returns>Whether the move happened; <see langword="false"/> when another writer changed a head first.</returns>
    private async Task<bool> TryMoveAsync(MoveEntry entry, Head moving, List<StoredVersion> moved, string? sourceETag,
        string? destinationETag, ResourceLocation destination, CancellationToken cancellationToken)
    {
        string entryKey = _keys.Move(Guid.NewGuid().ToString("N"));
        var mark = new HeadMove(entryKey[_keys.Moves.Length..], false);
        // Written first, so the sweep can undo whatever a try that stops leaves behind.
        if (!await TryPutAsync(entryKey, id => Serialize(entry with { WriteId = id }), null).ConfigureAwait(false))
            throw new InvalidOperationException($"The store already has a move entry {entryKey}.");

        MoveState? decided;
        try
        {
            bool pending = true;
            for (int i = 0; i < moved.Count && pending; i++)
            {
                StoredVersion version = moving.Versions[i];
                if (moved[i].Blob != version.Blob)
                {
                    pending = await CopyWhileRefreshingAsync(entryKey, _keys.Content(entry.SourceContainerId, version.Blob),
                        _keys.Content(entry.DestinationContainerId, moved[i].Blob), version.ContentLength, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (pending && ExpiryKeyOf(entry.DestinationContainerId, moved[i]) is { } expiryKey)
                    await PutEntryAsync(expiryKey, destination, cancellationToken).ConfigureAwait(false);
                if (pending && ProtectionKeyOf(entry.DestinationContainerId, moved[i]) is { } protectionKey)
                    await PutEntryAsync(protectionKey, destination, cancellationToken).ConfigureAwait(false);
            }
            bool marked = pending
                && await TryPutAsync(_keys.Head(entry.SourceContainerId, entry.SourceName),
                    id => Serialize(moving with { WriteId = id, Move = mark }), sourceETag).ConfigureAwait(false)
                && await TryPutAsync(_keys.Head(entry.DestinationContainerId, entry.DestinationName),
                    id => Serialize(new Head(moved, id, mark with { Into = true })), destinationETag).ConfigureAwait(false);
            // Not canceled from here, so a move whose heads are marked is always decided.
            decided = await DecideMoveAsync(entryKey, marked ? MoveState.Committed : MoveState.Abandoned, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Abandoned, unless the commit landed before the failure, in which case the move happened.
            (bool known, MoveState? state) = await DecideMoveQuietlyAsync(entryKey).ConfigureAwait(false);
            if (known && state is null) await UndoSweptTryAsync(entry, mark.Id).ConfigureAwait(false);
            else await FinishMoveQuietlyAsync(entryKey).ConfigureAwait(false);
            if (state == MoveState.Committed) return true;
            throw;
        }
        if (decided is null)
        {
            // The sweep mistook this try for one whose mover stopped, and finished it as abandoned.
            await UndoSweptTryAsync(entry, mark.Id).ConfigureAwait(false);
            return false;
        }
        await FinishMoveQuietlyAsync(entryKey).ConfigureAwait(false);
        return decided == MoveState.Committed;
    }

    /// <summary>
    /// Copies a version's content, writing the move's entry again every quarter of <see cref="StaleMoveAge"/> while it
    /// copies and once it's done, so the sweep doesn't mistake a long copy for a mover that stopped.
    /// </summary>
    /// <returns>Whether the move is still pending once the content is copied.</returns>
    private async Task<bool> CopyWhileRefreshingAsync(string entryKey, string sourceKey, string destinationKey, long length,
        CancellationToken cancellationToken)
    {
        using var copied = new CancellationTokenSource();
        Task<bool> refreshing = RefreshUntilAsync(entryKey, copied.Token);
        try
        {
            await CopyContentAsync(sourceKey, destinationKey, length, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await copied.CancelAsync().ConfigureAwait(false);
        }
        return await refreshing.ConfigureAwait(false) && await RefreshMoveAsync(entryKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a pending move's entry again every quarter of <see cref="StaleMoveAge"/> until <paramref name="stop"/>.
    /// A write that fails is tried again at the next turn.
    /// </summary>
    /// <returns>Whether the move was still pending at every write.</returns>
    private async Task<bool> RefreshUntilAsync(string entryKey, CancellationToken stop)
    {
        while (true)
        {
            try
            {
                // Waits on the clock the sweep tells an entry's age by.
                await Task.Delay(StaleMoveAge / 4, RealTime, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return true;
            }
            try
            {
                if (!await RefreshMoveAsync(entryKey, CancellationToken.None).ConfigureAwait(false)) return false;
            }
            catch (Exception exception) when (store.IsStoreFailure(exception))
            {
                // Tried again at the next turn.
            }
        }
    }

    /// <summary>Writes a pending move's entry again, unchanged, so the sweep sees the move is still being made.</summary>
    /// <returns>Whether the move is still pending.</returns>
    private async Task<bool> RefreshMoveAsync(string entryKey, CancellationToken cancellationToken)
    {
        Stored<MoveEntry> read = await GetAsync<MoveEntry>(entryKey, cancellationToken).ConfigureAwait(false);
        return read.Value is { State: MoveState.Pending } entry
            && await TryPutAsync(entryKey, id => Serialize(entry with { WriteId = id }), read.ETag).ConfigureAwait(false);
    }

    /// <summary>
    /// Once the sweep has finished a try as abandoned and deleted its entry, undoes what the try wrote: any marks it
    /// wrote, and what it wrote at the destination. What can't be undone stays; a mark left behind reads as abandoned.
    /// </summary>
    private async Task UndoSweptTryAsync(MoveEntry entry, string moveId)
    {
        try
        {
            await ClearMarkAsync(_keys.Head(entry.SourceContainerId, entry.SourceName), moveId, MoveState.Abandoned,
                CancellationToken.None).ConfigureAwait(false);
            await ClearMarkAsync(_keys.Head(entry.DestinationContainerId, entry.DestinationName), moveId, MoveState.Abandoned,
                CancellationToken.None).ConfigureAwait(false);
            await store.DeleteAsync(entry.DestinationKeys, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // Left as it is.
        }
    }

    /// <summary>Copies an object in the store, a part of <see cref="CopyPartSize"/> at a time.</summary>
    private Task CopyContentAsync(string sourceKey, string destinationKey, long length, CancellationToken cancellationToken) =>
        store.JoinAsync(destinationKey, [.. PartsOf(sourceKey, length)], cancellationToken);

    /// <summary>
    /// An object of <paramref name="length"/> bytes as parts to join, of <see cref="CopyPartSize"/> each but the last. A
    /// remainder shorter than <see cref="ObjectParts.PartSize"/> goes with the part before it, so no part but a join's
    /// last is shorter than that.
    /// </summary>
    private IEnumerable<ObjectRange> PartsOf(string key, long length)
    {
        long offset = 0;
        while (offset < length)
        {
            long size = Math.Min(CopyPartSize, length - offset);
            if (length - offset - size is > 0 and < ObjectParts.PartSize) size = length - offset;
            yield return new ObjectRange(key, offset, size);
            offset += size;
        }
    }

    /// <summary>
    /// Decides a move still pending as <paramref name="wanted"/>, unless another writer decides it first.
    /// </summary>
    /// <returns>How the move was decided, or <see langword="null"/> when its entry is gone, because the move is finished.</returns>
    private async Task<MoveState?> DecideMoveAsync(string entryKey, MoveState wanted, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            Stored<MoveEntry> read = await GetAsync<MoveEntry>(entryKey, cancellationToken).ConfigureAwait(false);
            if (read.Value is not { State: MoveState.Pending } entry) return read.Value?.State;
            if (await TryPutAsync(entryKey, id => Serialize(entry with { State = wanted, WriteId = id }), read.ETag).ConfigureAwait(false))
                return wanted;
            await WaitToRetryAsync(entryKey, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Abandons a move whose try failed, unless it was already decided.</summary>
    /// <returns>
    /// Whether the entry could be read, and how the move was decided, which is <see langword="null"/> when its entry is
    /// gone or couldn't be read.
    /// </returns>
    private async Task<(bool Known, MoveState? State)> DecideMoveQuietlyAsync(string entryKey)
    {
        try
        {
            return (true, await DecideMoveAsync(entryKey, MoveState.Abandoned, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Clears a decided move's marks from both heads, then deletes what it left on the side it didn't end up on, and
    /// its entry. A move still pending is left as it is.
    /// </summary>
    /// <returns>Whether it finished the move.</returns>
    private async Task<bool> FinishMoveAsync(string entryKey, CancellationToken cancellationToken)
    {
        if ((await GetAsync<MoveEntry>(entryKey, cancellationToken).ConfigureAwait(false)).Value is not { } entry
            || entry.State == MoveState.Pending)
        {
            return false;
        }
        string moveId = entryKey[_keys.Moves.Length..];
        await ClearMarkAsync(_keys.Head(entry.SourceContainerId, entry.SourceName), moveId, entry.State, cancellationToken)
            .ConfigureAwait(false);
        await ClearMarkAsync(_keys.Head(entry.DestinationContainerId, entry.DestinationName), moveId, entry.State,
            cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync(entry.State == MoveState.Committed ? entry.SourceKeys : entry.DestinationKeys, cancellationToken)
            .ConfigureAwait(false);
        await store.DeleteAsync([entryKey], cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Finishes a move its mover just decided; what that leaves undone is left to the sweep.</summary>
    private async Task FinishMoveQuietlyAsync(string entryKey)
    {
        try
        {
            await FinishMoveAsync(entryKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // The sweep finishes it.
        }
    }

    /// <summary>Writes a head a decided move marked as the move left it, if the head still has the move's mark.</summary>
    private async Task ClearMarkAsync(string key, string moveId, MoveState state, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            Stored<Head> read = await GetAsync<Head>(key, cancellationToken).ConfigureAwait(false);
            if (read.Value?.Move?.Id != moveId) return;
            // A head left with no versions keeps only a write ID, as any emptied head does.
            Head cleared = Decided(read.Value, state) ?? new Head([]);
            if (await TryPutAsync(key, id => Serialize(cleared with { WriteId = id, Move = null }), read.ETag).ConfigureAwait(false))
                return;
            ObjectStoreTelemetry.CountRetry(storeName);
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The head as readers see it: as stored, or for one a move marked, as the move's entry decides. It never writes.
    /// </summary>
    private async Task<Head?> ReadHeadAsync(string key, CancellationToken cancellationToken) =>
        (await ResolveHeadAsync(key, false, cancellationToken).ConfigureAwait(false)).Head;

    /// <summary>
    /// The head as stored, with its ETag for a conditional write, and as a writer may change it: for a head marked by a
    /// move that's still pending, the move is abandoned first, so it can't be committed once the head is changed.
    /// </summary>
    private Task<(Stored<Head> Read, Head? Head)> ReadHeadToChangeAsync(string key, CancellationToken cancellationToken) =>
        ResolveHeadAsync(key, true, cancellationToken);

    /// <summary>
    /// Reads a head, and for one a move marked, the move's entry, abandoning the move first if
    /// <paramref name="abandonPending"/> and it's still pending.
    /// </summary>
    private async Task<(Stored<Head> Read, Head? Head)> ResolveHeadAsync(string key, bool abandonPending,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            Stored<Head> read = await GetAsync<Head>(key, cancellationToken).ConfigureAwait(false);
            if (read.Value?.Move is not { } move) return (read, read.Value);
            string entryKey = _keys.Move(move.Id);
            MoveState? state = abandonPending
                ? await DecideMoveAsync(entryKey, MoveState.Abandoned, cancellationToken).ConfigureAwait(false)
                : (await GetAsync<MoveEntry>(entryKey, cancellationToken).ConfigureAwait(false)).Value?.State;
            // An entry that's gone usually belongs to a move finished since the head was read, which has cleared its
            // mark, so the head is read again.
            if (state is null && attempt < 3) continue;
            return (read, Decided(read.Value, state ?? MoveState.Abandoned));
        }
    }

    /// <summary>
    /// A head a move marked, as the move decided it: the versions are at the destination once it's committed, and at
    /// the source otherwise. A mark whose entry stays gone belongs to a try the sweep finished as abandoned before the try
    /// wrote the mark, since a finished move clears its marks before deleting its entry; it reads as abandoned.
    /// </summary>
    private static Head? Decided(Head head, MoveState state) =>
        head.Move!.Into == (state == MoveState.Committed) ? head with { Move = null } : null;

    /// <summary>
    /// Finishes the moves whose movers stopped before they could: it abandons those still pending, then clears the
    /// marks and deletes what's left, for every entry over an hour old. A move that can't be finished is added to
    /// <paramref name="failures"/> and left for the next sweep.
    /// </summary>
    private async Task SweepMovesAsync(List<Exception> failures, CancellationToken cancellationToken)
    {
        DateTimeOffset staleBefore = RealTime.GetUtcNow() - StaleMoveAge;
        await foreach (ObjectListing item in store.ListAsync(_keys.Moves, null, cancellationToken).ConfigureAwait(false))
        {
            if (item.LastModified >= staleBefore) continue;
            try
            {
                await DecideMoveAsync(item.Key, MoveState.Abandoned, cancellationToken).ConfigureAwait(false);
                if (await FinishMoveAsync(item.Key, cancellationToken).ConfigureAwait(false)) ObjectStoreTelemetry.CountRecoveredMove(storeName);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failures.Add(exception);
            }
        }
    }

    private static byte[] Serialize(MoveEntry entry) => JsonSerializer.SerializeToUtf8Bytes(entry);

    /// <summary>
    /// A move's entry: whether it's decided, both heads it changes, and what to delete once it's decided: on the side
    /// the versions leave, or, for a move abandoned, on the side they were to go to.
    /// </summary>
    private sealed record MoveEntry(
        [property: JsonPropertyName("s")] MoveState State,
        [property: JsonPropertyName("sc")] string SourceContainerId,
        [property: JsonPropertyName("sn")] string SourceName,
        [property: JsonPropertyName("dc")] string DestinationContainerId,
        [property: JsonPropertyName("dn")] string DestinationName,
        [property: JsonPropertyName("sk")] List<string> SourceKeys,
        [property: JsonPropertyName("dk")] List<string> DestinationKeys,
        [property: JsonPropertyName("w")] string? WriteId = null);

    private enum MoveState
    {
        Pending,
        Committed,
        Abandoned
    }

    /// <summary>A move's mark on a head: the move's ID, and whether the head is where the versions go.</summary>
    private sealed record HeadMove(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("in")] bool Into);
}
