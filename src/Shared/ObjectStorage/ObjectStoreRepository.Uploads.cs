using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Resourcefull.Providers;

namespace Resourcefull.ObjectStorage;

// Upload sessions: each has a record under {prefix}u/, outside its container's data, and its content in segments
// beside the record, each added to the record as it's stored. Every change to the record is a conditional write, as a
// head's is, so two appends from the same offset can't both land. Completing seals the record, so nothing more can be
// appended, joins the segments in the store into the version's content object, and adds the version with a head write.
internal sealed partial class ObjectStoreRepository
{
    /// <summary>
    /// The most an append stores before adding what it stored to the session's record, so an append that fails partway
    /// keeps what it stored. Tests set it lower, though above <see cref="ShortestSegment"/> and <see cref="PeekSize"/>
    /// together.
    /// </summary>
    internal int SegmentSize { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// A last segment shorter than this is stored again with the start of the next append, so every segment but the
    /// last is at least this long, as <see cref="IObjectStore.JoinAsync"/> needs.
    /// </summary>
    private const int ShortestSegment = ObjectParts.PartSize;

    /// <summary>How much of an append is read before a segment is started, to find out whether there's anything to store.</summary>
    private const int PeekSize = 81920;

    /// <summary>
    /// How old segments that no record names must be before the sweep deletes them. An append stores a segment before
    /// adding it to the record, so a younger one may belong to an append still running whose session was deleted
    /// meanwhile; that append deletes the segment itself when it finds the session gone.
    /// </summary>
    private static readonly TimeSpan StaleSegmentAge = TimeSpan.FromHours(1);

    public override async Task StartStagedVersionAsync(NewResourceVersion version, CancellationToken cancellationToken = default)
    {
        ResourceAddress location = version.Resource;
        ContainerRecord container = await GetContainerAsync(location.ContainerName, cancellationToken).ConfigureAwait(false)
            ?? throw ContainerMissing(location.ContainerName);
        var record = new UploadRecord(location.ContainerName, location.ResourceName, version.ContentType, version.DeclaredLength,
            new Dictionary<string, string>(version.Metadata), version.ExpiresAt, [], _clock.GetUtcNow())
        {
            RetainUntil = version.RetainUntil, LegalHold = version.LegalHold
        };
        if (!await TryPutAsync(_keys.UploadRecord(container.Id, version.VersionId), id => Serialize(record with { WriteId = id }), null)
            .ConfigureAwait(false))
        {
            throw new ResourceConflictException($"The container {location.ContainerName} already has an upload {version.VersionId}.")
            {
                ContainerName = location.ContainerName, ResourceName = location.ResourceName
            };
        }
    }

    public override async Task<StagedVersion?> GetStagedVersionAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default) =>
        (await GetUploadAsync(resource, versionId, cancellationToken).ConfigureAwait(false)).Record is { } record
            ? new StagedVersion(new NewResourceVersion(resource, versionId)
            {
                ContentType = record.ContentType,
                DeclaredLength = record.DeclaredLength,
                Metadata = new Dictionary<string, string>(record.Metadata),
                ExpiresAt = record.ExpiresAt,
                RetainUntil = record.RetainUntil,
                LegalHold = record.LegalHold
            }, record.Length)
            : null;

    /// <summary>
    /// Stores the content in segments of up to <see cref="SegmentSize"/>, adding each to the session's record once it's
    /// stored. A short last segment is read back and stored again with the start of the content, in its place. Once the
    /// segments take as many parts as the store joins, those since the last such join are joined into one in the store,
    /// so the session holds as much as one upload can.
    /// </summary>
    public override async Task<long?> AppendStagedContentAsync(ResourceAddress resource, string versionId, long offset, Stream content,
        CancellationToken cancellationToken = default)
    {
        (string? containerId, UploadRecord? record) = await GetUploadAsync(resource, versionId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        ThrowUnlessAppendable(resource, versionId, record, offset);

        long length = offset;
        byte[] peek = ArrayPool<byte>.Shared.Rent(PeekSize);
        byte[]? tailBytes = null;
        try
        {
            while (true)
            {
                Segment? tail = record.Segments is [.., { Length: < ShortestSegment } last] ? last : null;
                int tailLength = (int)(tail?.Length ?? 0);
                // Take no more than one object holds, so the session can always be completed.
                long room = store.MaxObjectLength - length;
                int peeked = room <= 0 ? 0
                    : await content.ReadAsync(peek.AsMemory(0, (int)Math.Min(PeekSize, room)), cancellationToken).ConfigureAwait(false);
                if (peeked == 0)
                {
                    if (room > 0 || await content.ReadAsync(peek.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
                        return length;
                    throw SessionFull(resource, versionId, length);
                }
                if (tail is null && PartCount(record.Segments) >= store.MaxJoinedParts)
                {
                    if (await JoinSegmentsAsync(resource, containerId!, versionId, length, record, cancellationToken).ConfigureAwait(false)
                        is not { } joined)
                        return null;
                    record = joined;
                }
                if (tail is not null)
                {
                    tailBytes ??= ArrayPool<byte>.Shared.Rent(ShortestSegment);
                    if (!await ReadSegmentAsync(containerId!, versionId, tail, tailBytes, cancellationToken).ConfigureAwait(false))
                    {
                        // Gone with its session, or stored again by another append from the same offset.
                        if ((await GetUploadAsync(resource, versionId, CancellationToken.None).ConfigureAwait(false)).Record is null)
                            return null;
                        throw await ChangedWhileReadAsync(resource, versionId).ConfigureAwait(false);
                    }
                }

                string token = Guid.NewGuid().ToString("N");
                string key = _keys.UploadSegment(containerId!, versionId, token);
                var segment = new SegmentStream(tail is null ? default : tailBytes.AsMemory(0, tailLength), peek.AsMemory(0, peeked),
                    content, Math.Min(SegmentSize - tailLength, room) - peeked);
                await store.UploadAsync(key, segment, cancellationToken).ConfigureAwait(false);
                long added = peeked + segment.FromContent;
                try
                {
                    record = await AddSegmentAsync(resource, containerId!, versionId, length, tail is null ? [] : [tail],
                        new Segment(token, tailLength + added)).ConfigureAwait(false);
                }
                catch
                {
                    await DeleteQuietlyAsync([key]).ConfigureAwait(false);
                    throw;
                }
                if (record is null)
                {
                    await DeleteQuietlyAsync([key]).ConfigureAwait(false);
                    return null;
                }
                if (tail is not null)
                    await DeleteQuietlyAsync([_keys.UploadSegment(containerId!, versionId, tail.Token)]).ConfigureAwait(false);
                length += added;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(peek);
            if (tailBytes is not null) ArrayPool<byte>.Shared.Return(tailBytes);
        }
    }

    /// <summary>Reads a short segment into the start of <paramref name="buffer"/>.</summary>
    /// <returns><see langword="false"/> when it's gone.</returns>
    private async Task<bool> ReadSegmentAsync(string containerId, string versionId, Segment segment, byte[] buffer,
        CancellationToken cancellationToken)
    {
        ObjectRead? read = await store.OpenReadAsync(_keys.UploadSegment(containerId, versionId, segment.Token), 0, segment.Length,
            segment.Length, cancellationToken).ConfigureAwait(false);
        if (read is null) return false;
        using (read.Owner)
        {
            await read.Body.ReadExactlyAsync(buffer.AsMemory(0, (int)segment.Length), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Adds a stored segment to the session's record, in place of the record's last segments, <paramref name="replacing"/>,
    /// whose content it holds (none when it's simply added), provided the session still ends at
    /// <paramref name="length"/>. It isn't canceled, since the segment is already stored.
    /// </summary>
    /// <returns>The changed record, or <see langword="null"/> when the session is gone.</returns>
    private async Task<UploadRecord?> AddSegmentAsync(ResourceAddress resource, string containerId, string versionId, long length,
        IReadOnlyList<Segment> replacing, Segment segment)
    {
        string key = _keys.UploadRecord(containerId, versionId);
        for (int attempt = 1; ; attempt++)
        {
            Stored<UploadRecord> upload = await GetAsync<UploadRecord>(key, CancellationToken.None).ConfigureAwait(false);
            if (upload.Value is not { } record || record.ResourceName != resource.ResourceName) return null;
            ThrowUnlessAppendable(resource, versionId, record, length);
            // Another append from the same offset may have joined these segments first, which leaves the length as it
            // was, so the segments are checked too.
            if (!record.Segments.TakeLast(replacing.Count).SequenceEqual(replacing))
                throw await ChangedWhileReadAsync(resource, versionId).ConfigureAwait(false);
            List<Segment> segments = [.. record.Segments.SkipLast(replacing.Count), segment];
            UploadRecord changed = record with { Segments = segments, StagedAt = _clock.GetUtcNow() };
            if (await TryPutAsync(key, id => Serialize(changed with { WriteId = id }), upload.ETag).ConfigureAwait(false)) return changed;

            ObjectStoreTelemetry.CountRetry(storeName);
            await WaitToRetryAsync(key, attempt, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void ThrowUnlessAppendable(ResourceAddress resource, string versionId, UploadRecord record, long offset)
    {
        if (record.Seal is null && record.Length == offset) return;
        string upload = $"The upload {versionId} of the resource {resource.ResourceName} in the container {resource.ContainerName}";
        throw new ResourceUploadOffsetException(record.Seal is null
            ? $"{upload} has {record.Length} bytes stored, not {offset}."
            : $"{upload} is being completed, so it takes no more content.", record.Length)
        {
            ContainerName = resource.ContainerName, ResourceName = resource.ResourceName
        };
    }

    /// <summary>
    /// Joins the session's last segments that take one part each, which are those since the last such join, into one
    /// segment in the store, and puts it in their place in the record, provided the session still ends at
    /// <paramref name="length"/>. Then it deletes the segments it replaced. A segment that takes more than one part, as a
    /// joined one usually does, is left as it is, so the session's content is copied about once however many joins it
    /// takes.
    /// </summary>
    /// <returns>The changed record, or <see langword="null"/> when the session is gone.</returns>
    /// <exception cref="ResourceUploadLimitException">The joined segments would still take as many parts as the store joins.</exception>
    private async Task<UploadRecord?> JoinSegmentsAsync(ResourceAddress resource, string containerId, string versionId, long length,
        UploadRecord record, CancellationToken cancellationToken)
    {
        Segment[] joining = [.. record.Segments.AsEnumerable().Reverse().TakeWhile(s => PartCount([s]) == 1).Reverse()];
        var segment = new Segment(Guid.NewGuid().ToString("N"), joining.Sum(s => s.Length));
        if (PartCount(record.Segments) - joining.Length + PartCount([segment]) >= store.MaxJoinedParts)
            throw SessionFull(resource, versionId, length);

        string key = _keys.UploadSegment(containerId, versionId, segment.Token);
        string[] keys = [.. joining.Select(s => _keys.UploadSegment(containerId, versionId, s.Token))];
        try
        {
            await store.JoinAsync(key, [.. joining.SelectMany((s, i) => PartsOf(keys[i], s.Length))], cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The segments are gone with their session, or joined and deleted by another append from the same offset.
            if ((await GetUploadAsync(resource, versionId, CancellationToken.None).ConfigureAwait(false)).Record is not { } current)
                return null;
            if (!current.Segments.SequenceEqual(record.Segments))
                throw await ChangedWhileReadAsync(resource, versionId).ConfigureAwait(false);
            throw;
        }
        UploadRecord? joined;
        try
        {
            joined = await AddSegmentAsync(resource, containerId, versionId, length, joining, segment).ConfigureAwait(false);
        }
        catch
        {
            await DeleteQuietlyAsync([key]).ConfigureAwait(false);
            throw;
        }
        if (joined is not null) ObjectStoreTelemetry.CountSessionJoin(storeName);
        await DeleteQuietlyAsync(joined is null ? [key] : keys).ConfigureAwait(false);
        return joined;
    }

    /// <summary>How many parts joining <paramref name="segments"/> takes.</summary>
    private int PartCount(IEnumerable<Segment> segments) => segments.Sum(s => PartsOf("", s.Length).Count());

    private static ResourceUploadLimitException SessionFull(ResourceAddress resource, string versionId, long length) =>
        new($"The upload {versionId} of the resource {resource.ResourceName} in the container {resource.ContainerName} holds " +
            $"{length} bytes, as much as the provider can store in one object.")
        {
            ContainerName = resource.ContainerName, ResourceName = resource.ResourceName
        };

    public override async Task<Stream?> GetStagedStreamAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default)
    {
        (string? containerId, UploadRecord? record) = await GetUploadAsync(resource, versionId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        return new StagedContentStream(record.Length, record.Segments.Count, async (i, ct) =>
        {
            Segment segment = record.Segments[i];
            // A segment that's gone was replaced by an append, or deleted with its session, since the record was read.
            return await store.OpenReadAsync(_keys.UploadSegment(containerId!, versionId, segment.Token), 0, segment.Length,
                segment.Length, ct).ConfigureAwait(false) ?? throw await ChangedWhileReadAsync(resource, versionId).ConfigureAwait(false);
        });
    }

    private async Task<ResourceUploadOffsetException> ChangedWhileReadAsync(ResourceAddress resource, string versionId)
    {
        long stored = (await GetUploadAsync(resource, versionId, CancellationToken.None).ConfigureAwait(false)).Record?.Length ?? 0;
        return new ResourceUploadOffsetException($"The upload {versionId} of the resource {resource.ResourceName} in the container " +
            $"{resource.ContainerName} changed while it was read, and has {stored} bytes stored.", stored)
        {
            ContainerName = resource.ContainerName, ResourceName = resource.ResourceName
        };
    }

    /// <summary>
    /// Seals the session, so nothing more is appended, then joins its segments into a content object of this
    /// completion's own and adds the version with a head write. If that's refused or fails, the session is unsealed, as
    /// it was. Another completion of the same session may run meanwhile, as when a client retries one that timed out:
    /// whichever adds the version first wins, and the other returns what the winner added.
    /// </summary>
    public override async Task<WrittenContent?> CompleteStagedVersionAsync(ResourceAddress resource, string versionId, long contentLength,
        string contentHash, ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default)
    {
        condition ??= new ResourceWriteCondition();
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return null;
        string recordKey = _keys.UploadRecord(container.Id, versionId);
        if (await SealAsync(resource, versionId, recordKey, contentLength, cancellationToken).ConfigureAwait(false)
            is not ({ } record, var seal))
        {
            return null;
        }

        string headKey = _keys.Head(container.Id, resource.ResourceName);
        string blob = Guid.NewGuid().ToString("N");
        string contentKey = _keys.Content(container.Id, blob);
        string? expiryToken = record.ExpiresAt is null ? null : Guid.NewGuid().ToString("N");
        string? expiryKey = record.ExpiresAt is { } expiresAt ? _keys.Expiry(expiresAt, versionId, expiryToken!, container.Id, blob) : null;
        string? protectionToken = record.RetainUntil is not null || record.LegalHold ? Guid.NewGuid().ToString("N") : null;
        string? protectionKey = protectionToken is null ? null : _keys.Protection(container.Id, protectionToken);
        string[] stored = [contentKey, .. new[] { expiryKey, protectionKey }.OfType<string>()];
        HashSet<string> searchKeys = SearchKeysOf(container.Id, resource.ResourceName, record.Metadata);
        List<string> replacedSearchKeys = [];
        WrittenContent? added = null;
        try
        {
            // Added by a completion that stopped before deleting the session, or by one that's still running. The read
            // includes the bin, since a deleted version's ID stays taken.
            Head? existing = await ReadStoredHeadAsync(headKey, cancellationToken).ConfigureAwait(false);
            if (Completed(existing, versionId) is { } completed)
            {
                await DeleteUploadQuietlyAsync(container.Id, versionId).ConfigureAwait(false);
                return completed;
            }
            // Checked again by the head write; this only spares joining content that would be refused.
            ThrowUnlessAddable(resource, existing, versionId, condition);

            await store.JoinAsync(contentKey,
                [.. record.Segments.SelectMany(s => PartsOf(_keys.UploadSegment(container.Id, versionId, s.Token), s.Length))],
                cancellationToken).ConfigureAwait(false);
            if (expiryKey is not null) await PutEntryAsync(expiryKey, resource, cancellationToken).ConfigureAwait(false);
            if (protectionKey is not null) await PutEntryAsync(protectionKey, resource, cancellationToken).ConfigureAwait(false);
            await PutSearchEntriesAsync(searchKeys, cancellationToken).ConfigureAwait(false);
            bool changed = await ChangeStoredHeadAsync(headKey, head =>
            {
                // Added by an earlier try of this write, which landed before another writer changed the head, or by
                // another completion. Either way, the version the head has is the one to report.
                if (Completed(head, versionId) is { } completed)
                {
                    added = completed;
                    return null;
                }
                ThrowUnlessAddable(resource, head, versionId, condition);
                replacedSearchKeys = [.. SearchKeysOf(container.Id, resource.ResourceName, head?.Versions.LastOrDefault()).Except(searchKeys)];
                added = new WrittenContent(contentLength, contentHash, _clock.GetUtcNow());
                return (head ?? new Head([])) with
                {
                    Versions = [.. head?.Versions ?? [], new StoredVersion(versionId, blob, contentLength, contentHash,
                        record.ContentType, new Dictionary<string, string>(record.Metadata), added.LastModified, record.ExpiresAt, null,
                        expiryToken, record.RetainUntil, record.LegalHold, protectionToken)]
                };
            }, cancellationToken).ConfigureAwait(false);
            // The content this completion joined is named by nothing, unless the head's version is its own, added by an
            // earlier try.
            if (!changed) await DeleteUnnamedContentAsync(headKey, blob, stored).ConfigureAwait(false);
        }
        catch
        {
            await DeleteUnnamedContentAsync(headKey, blob, stored).ConfigureAwait(false);
            // Another completion may have added the version meanwhile, as when this one's join found the segments
            // already deleted by it.
            if (await CompletedQuietlyAsync(headKey, versionId).ConfigureAwait(false) is { } completed)
            {
                await DeleteUploadQuietlyAsync(container.Id, versionId).ConfigureAwait(false);
                return completed;
            }
            if (seal is not null) await UnsealQuietlyAsync(recordKey, seal).ConfigureAwait(false);
            throw;
        }

        await DeleteUploadQuietlyAsync(container.Id, versionId).ConfigureAwait(false);
        // As for an upload: a container deleted meanwhile took the version with it.
        if (protectionKey is not null) await WaitForContainerDeleteAsync(container, resource.ContainerName).ConfigureAwait(false);
        else if ((await GetContainerAsync(resource.ContainerName, CancellationToken.None).ConfigureAwait(false))?.Id != container.Id)
        {
            await PurgeQuietlyAsync(container.Id).ConfigureAwait(false);
            throw ContainerMissing(resource.ContainerName);
        }
        await SettleSearchEntriesAsync(container.Id, resource.ResourceName, searchKeys, replacedSearchKeys).ConfigureAwait(false);
        return added!;
    }

    /// <summary>What a completion of the upload stored, if the name's head has its version.</summary>
    private static WrittenContent? Completed(Head? head, string versionId) =>
        head?.Versions.FirstOrDefault(v => v.VersionId == versionId) is { } version
            ? new WrittenContent(version.ContentLength, version.ContentHash, version.LastModified)
            : null;

    /// <summary>
    /// <see cref="Completed"/>, read from the store, after a completion failed; <see langword="null"/> if the head can't
    /// be read either.
    /// </summary>
    private async Task<WrittenContent?> CompletedQuietlyAsync(string headKey, string versionId)
    {
        try
        {
            return Completed(await ReadHeadAsync(headKey, CancellationToken.None).ConfigureAwait(false), versionId);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Seals the session's record, provided the session is <paramref name="length"/> long.</summary>
    /// <returns>
    /// The sealed record and the seal this call wrote, which is <see langword="null"/> when another completion had already
    /// sealed it; or <see langword="null"/> when there's no such session.
    /// </returns>
    private async Task<(UploadRecord Record, string? Seal)?> SealAsync(ResourceAddress resource, string versionId, string key, long length,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            Stored<UploadRecord> upload = await GetAsync<UploadRecord>(key, cancellationToken).ConfigureAwait(false);
            if (upload.Value is not { } record || record.ResourceName != resource.ResourceName) return null;
            if (record.Length != length)
            {
                throw new ResourceUploadOffsetException($"The upload {versionId} of the resource {resource.ResourceName} in the " +
                    $"container {resource.ContainerName} has {record.Length} bytes stored, not {length}.", record.Length)
                {
                    ContainerName = resource.ContainerName, ResourceName = resource.ResourceName
                };
            }
            // Sealed by a completion that stopped, or one still running, which this one may finish. Only the completion
            // that sealed it unseals it, so this one can't let appends in while that one is joining.
            if (record.Seal is not null) return (record, null);
            string seal = Guid.NewGuid().ToString("N");
            UploadRecord sealedRecord = record with { Seal = seal, StagedAt = _clock.GetUtcNow() };
            if (await TryPutAsync(key, id => Serialize(sealedRecord with { WriteId = id }), upload.ETag).ConfigureAwait(false))
                return (sealedRecord, seal);

            ObjectStoreTelemetry.CountRetry(storeName);
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Unseals a session whose completion was refused or failed, so it takes appends again, if it still has this
    /// completion's seal. If that fails, it stays sealed: it can still be completed or canceled.
    /// </summary>
    private async Task UnsealQuietlyAsync(string key, string seal)
    {
        try
        {
            Stored<UploadRecord> upload = await GetAsync<UploadRecord>(key, CancellationToken.None).ConfigureAwait(false);
            if (upload.Value is { } record && record.Seal == seal)
                await TryPutAsync(key, id => Serialize(record with { Seal = null, WriteId = id }), upload.ETag).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // Left sealed.
        }
    }

    /// <summary>Deletes the session's record, so the session is gone, then its segments.</summary>
    public override async Task<bool> DeleteStagedVersionAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default)
    {
        (string? containerId, UploadRecord? record) = await GetUploadAsync(resource, versionId, cancellationToken).ConfigureAwait(false);
        if (record is null) return false;
        await store.DeleteAsync([_keys.UploadRecord(containerId!, versionId)], cancellationToken).ConfigureAwait(false);
        await DeleteUploadQuietlyAsync(containerId!, versionId).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes a session's record, then everything else under it. What isn't deleted is left to
    /// <see cref="DeleteAbandonedUploadsAsync"/>.
    /// </summary>
    private async Task DeleteUploadQuietlyAsync(string containerId, string versionId)
    {
        try
        {
            await store.DeleteAsync([_keys.UploadRecord(containerId, versionId)], CancellationToken.None).ConfigureAwait(false);
            var keys = new List<string>();
            await foreach (ObjectListing item in store.ListAsync(_keys.Upload(containerId, versionId), null, CancellationToken.None)
                .ConfigureAwait(false))
            {
                keys.Add(item.Key);
            }
            await store.DeleteAsync(keys, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // Left for the sweep.
        }
    }

    /// <summary>
    /// Lists every session, and deletes those nothing was appended to since <paramref name="staleBefore"/>, those whose
    /// container was deleted, and segments no record names once they're an hour old: what a cancel or completion left
    /// when deleting them failed. A session that can't be deleted is left for the next sweep, and the others still are.
    /// It also finishes the moves whose movers stopped before they could, which are left behind the same way. Once a
    /// day, it also deletes content no head names, which writes and deletes that stopped partway leave behind, and finishes
    /// purging deleted containers.
    /// </summary>
    public override async Task<int> DeleteAbandonedUploadsAsync(DateTimeOffset staleBefore, CancellationToken cancellationToken = default)
    {
        int deleted = 0;
        var failures = new List<Exception>();
        var containerIds = new Dictionary<string, string?>();
        string? session = null;
        var keys = new List<ObjectListing>();
        async Task SweepAsync()
        {
            try
            {
                if (await SweepUploadAsync(session!, keys, staleBefore, containerIds, cancellationToken).ConfigureAwait(false)) deleted++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failures.Add(exception);
            }
            keys.Clear();
        }

        await foreach (ObjectListing item in store.ListAsync(_keys.Uploads, null, cancellationToken).ConfigureAwait(false))
        {
            string of = _keys.UploadOf(item.Key);
            if (of != session && session is not null) await SweepAsync().ConfigureAwait(false);
            session = of;
            keys.Add(item);
        }
        if (session is not null) await SweepAsync().ConfigureAwait(false);
        await SweepMovesAsync(failures, cancellationToken).ConfigureAwait(false);
        try
        {
            deleted += await SweepUnnamedContentAsync(staleBefore, failures, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failures.Add(exception);
        }
        ObjectStoreTelemetry.CountAbandonedUploads(deleted, storeName);

        if (failures.Count > 0)
        {
            throw new AggregateException($"{failures.Count} abandoned upload sessions, unfinished moves or scans for unnamed " +
                $"content failed, and are left for the next sweep; {deleted} sessions or content objects were deleted.", failures);
        }
        return deleted;
    }

    /// <summary>Deletes one session's objects if it's abandoned.</summary>
    /// <returns>Whether a session with a record was deleted.</returns>
    private async Task<bool> SweepUploadAsync(string session, List<ObjectListing> items, DateTimeOffset staleBefore,
        Dictionary<string, string?> containerIds, CancellationToken cancellationToken)
    {
        string recordKey = session + "r";
        if (items.All(i => i.Key != recordKey))
        {
            if (items.All(i => i.LastModified < RealTime.GetUtcNow() - StaleSegmentAge))
                await store.DeleteAsync([.. items.Select(i => i.Key)], cancellationToken).ConfigureAwait(false);
            return false;
        }

        if ((await GetAsync<UploadRecord>(recordKey, cancellationToken).ConfigureAwait(false)).Value is not { } record) return false;
        if (!containerIds.TryGetValue(record.ContainerName, out string? currentId))
        {
            currentId = (await GetContainerAsync(record.ContainerName, cancellationToken).ConfigureAwait(false))?.Id;
            containerIds[record.ContainerName] = currentId;
        }
        if (record.StagedAt >= staleBefore && session.StartsWith(_keys.Uploads + currentId + "/", StringComparison.Ordinal)) return false;

        await store.DeleteAsync([recordKey], cancellationToken).ConfigureAwait(false);
        await store.DeleteAsync([.. items.Select(i => i.Key).Where(k => k != recordKey)], cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A session's container ID and record, or a <see langword="null"/> record when the container has no session with
    /// that ID for the name.
    /// </summary>
    private async Task<(string? ContainerId, UploadRecord? Record)> GetUploadAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container)
            return (null, null);
        UploadRecord? record = (await GetAsync<UploadRecord>(_keys.UploadRecord(container.Id, versionId), cancellationToken)
            .ConfigureAwait(false)).Value;
        return (container.Id, record?.ResourceName == resource.ResourceName ? record : null);
    }

    private static byte[] Serialize(UploadRecord record) => JsonSerializer.SerializeToUtf8Bytes(record);

    /// <summary>
    /// An upload session's record: the version as it was staged, its segments in order, when content last arrived, and,
    /// while it's being completed, the seal of the completion that sealed it.
    /// </summary>
    private sealed record UploadRecord(
        [property: JsonPropertyName("c")] string ContainerName,
        [property: JsonPropertyName("r")] string ResourceName,
        [property: JsonPropertyName("t")] string? ContentType,
        [property: JsonPropertyName("l")] long? DeclaredLength,
        [property: JsonPropertyName("m")] Dictionary<string, string> Metadata,
        [property: JsonPropertyName("x")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("p")] List<Segment> Segments,
        [property: JsonPropertyName("at")] DateTimeOffset StagedAt,
        [property: JsonPropertyName("s")] string? Seal = null,
        [property: JsonPropertyName("w")] string? WriteId = null)
    {
        /// <summary>Until when the version will be retained once the session completes.</summary>
        [JsonPropertyName("ru"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? RetainUntil { get; init; }

        /// <summary>Whether the version will be under a legal hold once the session completes.</summary>
        [JsonPropertyName("lh"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool LegalHold { get; init; }

        /// <summary>How many bytes the session has stored.</summary>
        [JsonIgnore]
        public long Length => Segments.Sum(s => s.Length);
    }

    /// <summary>One segment of a session's content: the token its key ends with, and its length.</summary>
    private sealed record Segment(
        [property: JsonPropertyName("k")] string Token,
        [property: JsonPropertyName("n")] long Length);

    /// <summary>
    /// What one segment stores: <paramref name="tail"/>, the short segment it replaces, then <paramref name="peeked"/>
    /// and up to <paramref name="limit"/> more bytes of an append's content.
    /// </summary>
    private sealed class SegmentStream(ReadOnlyMemory<byte> tail, ReadOnlyMemory<byte> peeked, Stream content, long limit) : Stream
    {
        private ReadOnlyMemory<byte> _start = tail;
        private ReadOnlyMemory<byte> _next = peeked;

        /// <summary>How many bytes it has read from the content.</summary>
        public long FromContent { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (TakeStart(buffer) is var taken and > 0) return taken;
            int read = content.Read(buffer[..Remaining(buffer.Length)]);
            FromContent += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (TakeStart(buffer.Span) is var taken and > 0) return taken;
            int read = await content.ReadAsync(buffer[..Remaining(buffer.Length)], cancellationToken).ConfigureAwait(false);
            FromContent += read;
            return read;
        }

        private int TakeStart(Span<byte> buffer)
        {
            if (_start.IsEmpty) (_start, _next) = (_next, default);
            int taken = Math.Min(buffer.Length, _start.Length);
            _start.Span[..taken].CopyTo(buffer);
            _start = _start[taken..];
            return taken;
        }

        private int Remaining(int wanted) => (int)Math.Min(wanted, limit - FromContent);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// A session's content, read one segment after another, each opened by <paramref name="open"/> as it's reached.
    /// It reads only asynchronously, as the clients read it.
    /// </summary>
    private sealed class StagedContentStream(long length, int segments, Func<int, CancellationToken, Task<ObjectRead>> open) : Stream
    {
        private int _next;
        private ObjectRead? _read;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_read is null)
                {
                    if (_next == segments) return 0;
                    _read = await open(_next++, cancellationToken).ConfigureAwait(false);
                }
                int read = await _read.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0 || buffer.IsEmpty)
                {
                    _position += read;
                    return read;
                }
                _read.Owner.Dispose();
                _read = null;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _read?.Owner.Dispose();
            base.Dispose(disposing);
        }
    }
}
