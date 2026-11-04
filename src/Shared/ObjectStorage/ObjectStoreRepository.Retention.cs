using Resourcefull.Providers;

namespace Resourcefull.ObjectStorage;

// Retention and legal holds: a version that's had either gets a protection entry under its container's data, written
// before the head that names it and checked against the head when read, as expiry entries are. A container delete reads
// them to find whether anything in the container is protected. It first marks the container as closing, and a write
// that protects a version waits, once its head is written, until that delete decides. So a protection either lands in
// time for the delete to find it, and the delete refuses, or finds the container gone, and fails.
internal sealed partial class ObjectStoreRepository
{
    /// <summary>
    /// How long a container stays closing before a write that protects a version takes the delete that closed it as
    /// stopped, and reopens it: a delete only reads the protection entries while it's closing.
    /// </summary>
    private static readonly TimeSpan StaleClosingAge = TimeSpan.FromMinutes(1);

    /// <summary>How often a write that protects a version reads a closing container's record again.</summary>
    private static readonly TimeSpan ClosingPollInterval = TimeSpan.FromMilliseconds(50);

    public override bool SupportsRetention => true;

    public override Task<bool> SetRetentionAsync(ResourceLocation resource, string? versionId, DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default) =>
        ProtectAsync(resource, versionId, version => version.RetainUntil > retainUntil
            ? throw new ArgumentOutOfRangeException(nameof(retainUntil), retainUntil, $"The version {version.VersionId} of the resource " +
                $"{resource.ResourceName} in the container {resource.ContainerName} is retained until {version.RetainUntil:O}, and that can't move earlier.")
            : version with { RetainUntil = retainUntil }, cancellationToken);

    public override Task<bool> SetLegalHoldAsync(ResourceLocation resource, string? versionId, bool hold,
        CancellationToken cancellationToken = default) =>
        hold
            ? ProtectAsync(resource, versionId, version => version with { LegalHold = true }, cancellationToken)
            : ChangeVersionAsync(resource, versionId, version => version with { LegalHold = false }, cancellationToken);

    /// <summary>
    /// Writes a protection entry for the version, unless it has one, then changes it with <paramref name="protect"/> in
    /// a head write, and waits for a container delete that's deciding.
    /// </summary>
    private async Task<bool> ProtectAsync(ResourceLocation resource, string? versionId, Func<StoredVersion, StoredVersion> protect,
        CancellationToken cancellationToken)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return false;
        string headKey = _keys.Head(container.Id, resource.ResourceName);
        // A version's blob never changes, so the entry can be written for the version as it's read now.
        Head? read = await ReadHeadAsync(headKey, cancellationToken).ConfigureAwait(false);
        StoredVersion? target = versionId is null ? read?.Versions[^1] : read?.Versions.FirstOrDefault(v => v.VersionId == versionId);
        if (target is null) return false;
        string? token = target.ProtectionToken is null ? Guid.NewGuid().ToString("N") : null;
        if (token is not null) await PutEntryAsync(_keys.Protection(container.Id, token), resource, cancellationToken).ConfigureAwait(false);

        string? tokenUsed = null;
        bool changed = await ChangeHeadAsync(headKey, head =>
        {
            int index = head?.Versions.FindIndex(v => v.Blob == target.Blob) ?? -1;
            if (index < 0) return null;
            List<StoredVersion> versions = [.. head!.Versions];
            tokenUsed = versions[index].ProtectionToken ?? token;
            versions[index] = protect(versions[index]) with { ProtectionToken = tokenUsed };
            return new Head(versions);
        }, cancellationToken).ConfigureAwait(false);
        if (token is not null && tokenUsed != token) await DeleteQuietlyAsync([_keys.Protection(container.Id, token)]).ConfigureAwait(false);
        if (changed) await WaitForContainerDeleteAsync(container, resource.ContainerName).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// After a write that left a version protected, waits while a container delete that may not have seen it decides:
    /// one that found it reopens the container, and one that didn't deletes it, and the write then fails as a write to a
    /// container deleted meanwhile does.
    /// </summary>
    /// <exception cref="ResourceContainerNotFoundException">The container was deleted, protected version and all.</exception>
    private async Task WaitForContainerDeleteAsync(ContainerRecord container, string containerName)
    {
        string key = _keys.Container(containerName);
        for (int attempt = 1; ; attempt++)
        {
            Stored<ContainerRecord> read = await GetAsync<ContainerRecord>(key, CancellationToken.None).ConfigureAwait(false);
            if (read.Value is not { Deleted: false } record || record.Id != container.Id)
            {
                await PurgeQuietlyAsync(container.Id).ConfigureAwait(false);
                throw ContainerMissing(containerName);
            }
            if (record.ClosingSince is not { } since) return;
            if (since < RealTime.GetUtcNow() - StaleClosingAge)
            {
                // The delete that closed it stopped, or it's taking so long that it would miss this version: reopen the
                // container. That makes the delete's write of its deleted mark fail, so it reads the entries again.
                await TryPutAsync(key, id => Serialize(record with { ClosingSince = null, WriteId = id }), read.ETag).ConfigureAwait(false);
                continue;
            }
            await Task.Delay(ClosingPollInterval, RealTime, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A version in the container that can't be deleted now, found from its protection entries; <see langword="null"/>
    /// when there's none.
    /// </summary>
    private async Task<(ResourceLocation Location, StoredVersion Version)?> FindProtectedAsync(string containerId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        string prefix = _keys.Protections(containerId);
        await foreach (string[] keys in ListKeysAsync(prefix, null, _ => true, () => int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            var found = new (ResourceLocation, StoredVersion)?[keys.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, keys.Length),
                new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
                async (i, ct) =>
                {
                    if ((await GetAsync<ExpiryEntry>(keys[i], ct).ConfigureAwait(false)).Value is not { } named) return;
                    string token = keys[i][prefix.Length..];
                    Head? head = await ReadHeadAsync(_keys.Head(containerId, named.ResourceName), ct).ConfigureAwait(false);
                    if (head?.Versions.FirstOrDefault(v => v.ProtectionToken == token) is { } version && !IsDeletable(version, now))
                        found[i] = (new ResourceLocation(named.ContainerName, named.ResourceName), version);
                }).ConfigureAwait(false);
            if (found.FirstOrDefault(f => f is not null) is { } first) return first;
        }
        return null;
    }

    /// <summary>
    /// Clears the closing mark a container delete set, once it has found a protected version, unless something else
    /// changed the record first: a write that reopened it, or another delete.
    /// </summary>
    private async Task ReopenQuietlyAsync(string key, string containerId)
    {
        try
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                Stored<ContainerRecord> read = await GetAsync<ContainerRecord>(key, CancellationToken.None).ConfigureAwait(false);
                if (read.Value is not { Deleted: false, ClosingSince: not null } record || record.Id != containerId) return;
                if (await TryPutAsync(key, id => Serialize(record with { ClosingSince = null, WriteId = id }), read.ETag).ConfigureAwait(false))
                    return;
            }
        }
        catch (Exception exception) when (store.IsStoreFailure(exception))
        {
            // Left closing: writes that protect a version wait until the mark is stale, and then reopen it.
        }
    }

    /// <summary>Whether a stored version may be deleted at <paramref name="now"/>: it's neither held nor retained.</summary>
    private static bool IsDeletable(StoredVersion version, DateTimeOffset now) => !version.LegalHold && !(version.RetainUntil > now);

    /// <summary>A version's protection entry's key, or <see langword="null"/> when it's never been protected.</summary>
    private string? ProtectionKeyOf(string containerId, StoredVersion version) =>
        version.ProtectionToken is { } token ? _keys.Protection(containerId, token) : null;
}
