using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Resourcefull.Providers;

namespace Resourcefull;

// Imports from a stream of versions, and copies to another store through its import. A copy compares containers by
// their version lists, version by version, never by totals: each version's ID, content hash and length, type, times,
// metadata, origin, expiry, retention date and hold, and the container's metadata. Times are compared to the
// microsecond, the finest every provider keeps.
internal partial class ResourceServiceClient
{
    /// <summary>
    /// The container metadata that says when an import last wrote its mark, which a running one does every
    /// <see cref="ImportMarkInterval"/>, so a copy can tell an import still running from one that stopped.
    /// </summary>
    internal const string ImportMarkedAtKey = "resourcefull.import_at";

    /// <summary>How often a running import writes its mark again.</summary>
    internal static readonly TimeSpan ImportMarkInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long since an import last wrote its mark before a copy takes it as stopped.</summary>
    internal static readonly TimeSpan StaleImportAge = TimeSpan.FromMinutes(30);

    /// <summary>How many differences the exception for a container that doesn't match lists.</summary>
    private const int DifferencesNamed = 5;

    public Task<ContainerImportResult> ImportContainerAsync(string containerName, IAsyncEnumerable<ImportedVersion> versions,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("import", containerName, null, activity =>
        {
            ResourceNames.ThrowIfInvalidNewContainerName(containerName);
            ArgumentNullException.ThrowIfNull(versions);
            return ImportNewContainerAsync(containerName, WithoutImportMark(metadata ?? new Dictionary<string, string>()),
                versions, "the source", activity, cancellationToken);
        });

    public Task<ContainerImportResult> ImportVersionsAsync(string containerName, IAsyncEnumerable<ImportedVersion> versions,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("import_versions", containerName, null, async activity =>
        {
            ResourceNames.ThrowIfInvalidContainerName(containerName);
            ArgumentNullException.ThrowIfNull(versions);
            if (await resourceRepository.GetContainerInfoAsync(containerName, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new ResourceContainerNotFoundException($"The container {containerName} doesn't exist.")
                {
                    ContainerName = containerName
                };
            }
            ContainerImportResult imported = await ContainerArchive.ImportAsync(versions, containerName, newContainer: false,
                "the source", resourceRepository, _options, policies, _telemetry, uploadInspector, cancellationToken)
                .ConfigureAwait(false);
            activity?.SetTag("resourcefull.resources", imported.ResourceCount);
            activity?.SetTag("resourcefull.versions", imported.VersionCount);
            return imported;
        });

    /// <summary>
    /// Creates a container and imports <paramref name="versions"/> into it, marked unfinished while it fills, and deletes
    /// it again if anything fails, so nothing is left half-imported.
    /// </summary>
    private async Task<ContainerImportResult> ImportNewContainerAsync(string containerName, Dictionary<string, string> metadata,
        IAsyncEnumerable<ImportedVersion> versions, string source, Activity? activity, CancellationToken cancellationToken)
    {
        await CreateContainerCoreAsync(containerName, metadata, cancellationToken).ConfigureAwait(false);
        ContainerImportResult imported;
        try
        {
            // Marked while it fills, so a container whose import never finished, such as when the application stopped
            // partway, can be told from a finished one, and written again as it goes, so a copy can tell it's still
            // running. A provider that can't change metadata goes unmarked.
            bool marked = await TrySetMetadataAsync(containerName, Marked(metadata), cancellationToken).ConfigureAwait(false);
            using var done = new CancellationTokenSource();
            Task keeping = marked ? KeepMarkedAsync(containerName, metadata, activity, done.Token) : Task.CompletedTask;
            try
            {
                imported = await ContainerArchive.ImportAsync(versions, containerName, newContainer: true, source,
                    resourceRepository, _options, policies, _telemetry, uploadInspector, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await done.CancelAsync().ConfigureAwait(false);
                // Waited for, so a write of the mark still going doesn't land after the mark is cleared.
                await keeping.ConfigureAwait(false);
            }
            if (marked) await resourceRepository.SetContainerMetadataAsync(containerName, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // Nothing is left half-imported; the caller's token may be what stopped it, so it isn't used here.
            try
            {
                await resourceRepository.DeleteContainerAsync(containerName, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException($"The import of {containerName} failed, and deleting what it had imported failed too: " +
                    "delete the container before importing it again.", failure, cleanupFailure);
            }

            throw;
        }

        activity?.SetTag("resourcefull.resources", imported.ResourceCount);
        activity?.SetTag("resourcefull.versions", imported.VersionCount);
        return imported;
    }

    /// <summary>The container's metadata with the mark of an import still filling it, written now.</summary>
    private Dictionary<string, string> Marked(Dictionary<string, string> metadata) => new(metadata)
    {
        [ImportMarkerKey] = ImportMarkerValue,
        [ImportMarkedAtKey] = (timeProvider ?? TimeProvider.System).GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
    };

    /// <summary><paramref name="metadata"/> without the mark of an import, which a container being copied may have.</summary>
    private static Dictionary<string, string> WithoutImportMark(IEnumerable<KeyValuePair<string, string>> metadata)
    {
        var copy = new Dictionary<string, string>(metadata);
        copy.Remove(ImportMarkerKey);
        copy.Remove(ImportMarkedAtKey);
        return copy;
    }

    /// <summary>
    /// Writes an import's mark again every <see cref="ImportMarkInterval"/> until <paramref name="stop"/>; a write that
    /// fails, a timeout included, is recorded on the import's span and tried again at the next turn. Each write runs to
    /// its end, so none lands after the import clears the mark.
    /// </summary>
    private async Task KeepMarkedAsync(string containerName, Dictionary<string, string> metadata, Activity? activity,
        CancellationToken stop)
    {
        while (true)
        {
            try
            {
                await Task.Delay(ImportMarkInterval, timeProvider ?? TimeProvider.System, stop).ConfigureAwait(false);
                await resourceRepository.SetContainerMetadataAsync(containerName, Marked(metadata), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Tried again at the next turn; a copy that finds the mark old takes the import as stopped.
                _telemetry.RecordException(activity, exception);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="metadata"/> marks an import still filling its container: <see langword="null"/> when it
    /// doesn't, <see langword="true"/> when the import wrote its mark within <see cref="StaleImportAge"/>, and
    /// <see langword="false"/> when it's older, or from a release that didn't write the time, and the import stopped.
    /// </summary>
    private bool? ImportRunning(IReadOnlyDictionary<string, string> metadata)
    {
        if (!metadata.TryGetValue(ImportMarkerKey, out string? state) || state != ImportMarkerValue) return null;
        return metadata.TryGetValue(ImportMarkedAtKey, out string? at)
            && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset markedAt)
            && markedAt > (timeProvider ?? TimeProvider.System).GetUtcNow() - StaleImportAge;
    }

    public Task<ContainerCopyResult> CopyContainerToAsync(string containerName, IResourceServiceClient destination,
        string? destinationContainerName = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("copy_container", containerName, null, async activity =>
        {
            ResourceNames.ThrowIfInvalidContainerName(containerName);
            ArgumentNullException.ThrowIfNull(destination);
            string destinationName = destinationContainerName ?? containerName;
            if (destination.StoreName is { } store) activity?.SetTag("resourcefull.destination_store", store);
            ContainerInfo container = await resourceRepository.GetContainerInfoAsync(containerName, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceContainerNotFoundException($"The container {containerName} doesn't exist.")
                {
                    ContainerName = containerName
                };
            ThrowIfUnfinished(container);

            ContainerImportResult imported = await destination.ImportContainerAsync(destinationName,
                VersionsOfAsync(containerName, cancellationToken), WithoutImportMark(container.Metadata), cancellationToken).ConfigureAwait(false);
            activity?.SetTag("resourcefull.resources", imported.ResourceCount);
            activity?.SetTag("resourcefull.versions", imported.VersionCount);
            _telemetry.CountCopied("imported", imported.VersionCount);
            return new ContainerCopyResult(containerName, destinationName, imported.ResourceCount, imported.VersionCount,
                imported.ContentSize);
        }, "resourcefull.destination_container", destinationContainerName ?? containerName);

    /// <summary>
    /// Throws if a container in this store still has the mark of a copy or import that hasn't finished, whether it's
    /// running or stopped. Such a container may not be whole, and a copy of it, which doesn't carry the mark, would look
    /// finished.
    /// </summary>
    private void ThrowIfUnfinished(ContainerInfo container)
    {
        if (ImportRunning(container.Metadata) is not null)
        {
            throw Conflict(container.Name, $"The container {container.Name} is from a copy or import that hasn't finished, so it may " +
                $"not be whole. Let it finish before copying this container; if it stopped, finish a copy with {nameof(ExistingContainers)}." +
                $"{nameof(ExistingContainers.Update)} from its source, or delete the container and copy or import it again.");
        }
    }

    /// <summary>
    /// Every version in a container, a page of names at a time, each name's oldest first, as an export reads them; each
    /// opens its content from this store when the destination reads it.
    /// </summary>
    private async IAsyncEnumerable<ImportedVersion> VersionsOfAsync(string containerName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (IReadOnlyList<ResourceInfo> page in VersionPagesAsync(containerName, cancellationToken).ConfigureAwait(false))
        {
            foreach (IGrouping<string, ResourceInfo> name in page.GroupBy(v => v.Name, StringComparer.Ordinal))
            {
                foreach (ResourceInfo version in name.Reverse()) yield return ToImport(containerName, version);
            }
        }
    }

    /// <summary>A container's versions, a page of names at a time, as <see cref="ResourceRepository.GetVersionPageAsync"/> lists them.</summary>
    private async IAsyncEnumerable<IReadOnlyList<ResourceInfo>> VersionPagesAsync(string containerName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? afterName = null;
        while (true)
        {
            IReadOnlyList<ResourceInfo> page = await resourceRepository.GetVersionPageAsync(containerName, afterName,
                ContainerArchive.NamesPerPage, cancellationToken).ConfigureAwait(false);
            // Only an empty page ends it: a page can hold fewer names than asked for, and more still follow.
            if (page.Count == 0) yield break;
            yield return page;
            afterName = page[^1].Name;
        }
    }

    /// <summary>One of this store's versions, to import elsewhere.</summary>
    private ImportedVersion ToImport(string containerName, ResourceInfo version) =>
        new(version, ct => resourceRepository.GetResourceStreamAsync(new ResourceAddress(containerName, version.Name),
            version.VersionId, ct));

    public Task<StoreCopyResult> CopyStoreToAsync(IResourceServiceClient destination, StoreCopyOptions? options = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("copy_store", null, null, async activity =>
        {
            ArgumentNullException.ThrowIfNull(destination);
            options ??= new StoreCopyOptions();
            if (!Enum.IsDefined(options.Existing))
                throw new ArgumentOutOfRangeException(nameof(options), options.Existing, $"{options.Existing} isn't an {nameof(ExistingContainers)} value.");
            if (!Enum.IsDefined(options.OnFailure))
                throw new ArgumentOutOfRangeException(nameof(options), options.OnFailure, $"{options.OnFailure} isn't a {nameof(StoreCopyFailures)} value.");
            string? prefix = string.IsNullOrEmpty(options.Prefix) ? null : options.Prefix;
            if (destination.StoreName is { } store) activity?.SetTag("resourcefull.destination_store", store);

            var copied = new List<ContainerCopyResult>();
            var updated = new List<ContainerUpdateResult>();
            var matching = new List<string>();
            var failed = new List<ContainerCopyFailure>();
            void Tag()
            {
                activity?.SetTag("resourcefull.copied", copied.Count);
                activity?.SetTag("resourcefull.updated", updated.Count);
                activity?.SetTag("resourcefull.matching", matching.Count);
                activity?.SetTag("resourcefull.failed", failed.Count);
            }

            await foreach (ContainerInfo container in ((IResourceServiceClient)this).ListContainersAsync(new ContainerListOptions { Prefix = prefix }, cancellationToken)
                .ConfigureAwait(false))
            {
                ContainerCopyOutcome outcome;
                try
                {
                    outcome = await CopyOneAsync(container, destination, options.Existing, copied, updated, matching, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failed.Add(new ContainerCopyFailure(container.Name, exception));
                    if (options.OnFailure == StoreCopyFailures.Stop)
                    {
                        Tag();
                        throw new StoreCopyException($"The copy stopped at the container {container.Name}, which failed: {exception.Message}",
                            new StoreCopyResult(copied, updated, matching, failed, []), exception);
                    }
                    outcome = ContainerCopyOutcome.Failed;
                }
                options.Progress?.Report(new ContainerCopied(container.Name, outcome));
            }

            var destinationOnly = new List<string>();
            await foreach (ContainerInfo theirs in destination.ListContainersAsync(new ContainerListOptions { Prefix = prefix }, cancellationToken)
                .ConfigureAwait(false))
            {
                if (await resourceRepository.GetContainerInfoAsync(theirs.Name, cancellationToken).ConfigureAwait(false) is null)
                    destinationOnly.Add(theirs.Name);
            }
            Tag();
            return new StoreCopyResult(copied, updated, matching, failed, destinationOnly);
        });

    /// <summary>
    /// Copies one container, or does what <paramref name="existing"/> says with one the destination has, and adds what it
    /// did to <paramref name="copied"/>, <paramref name="updated"/> or <paramref name="matching"/>.
    /// </summary>
    /// <exception cref="ResourceConflictException">The container can't be copied, or the destination's doesn't match.</exception>
    private async Task<ContainerCopyOutcome> CopyOneAsync(ContainerInfo container, IResourceServiceClient destination,
        ExistingContainers existing, List<ContainerCopyResult> copied, List<ContainerUpdateResult> updated, List<string> matching,
        CancellationToken cancellationToken)
    {
        ThrowIfUnfinished(container);
        ContainerInfo? theirs = await destination.GetContainerClient(container.Name).GetInfoAsync(cancellationToken).ConfigureAwait(false);
        if (theirs is null)
        {
            copied.Add(await CopyContainerToAsync(container.Name, destination, null, cancellationToken).ConfigureAwait(false));
            return ContainerCopyOutcome.Copied;
        }

        bool? running = ImportRunning(theirs.Metadata);
        if (running == true)
            throw Conflict(container.Name, $"The destination's container {container.Name} is still being copied or imported; try again once it finishes.");
        if (existing == ExistingContainers.Fail)
            throw Conflict(container.Name, $"The destination already has a container {container.Name}: {nameof(ExistingContainers)}.{nameof(ExistingContainers.SkipMatching)} " +
                $"checks it and {nameof(ExistingContainers.Update)} brings it up to date.");
        if (running == false && existing == ExistingContainers.SkipMatching)
        {
            throw Conflict(container.Name, $"The destination's container {container.Name} is from a copy or import that stopped " +
                $"partway: {nameof(ExistingContainers)}.{nameof(ExistingContainers.Update)} finishes it.");
        }

        var comparison = new ContainerComparison(this, resourceRepository, container, theirs, destination);
        if (existing == ExistingContainers.SkipMatching)
        {
            List<string> differences = await comparison.FindDifferencesAsync(cancellationToken).ConfigureAwait(false);
            if (differences.Count == 0)
            {
                matching.Add(container.Name);
                return ContainerCopyOutcome.Matching;
            }
            throw Conflict(container.Name, $"The destination's container {container.Name} doesn't match the source's: " +
                string.Join("; ", differences) + (differences.Count == DifferencesNamed ? "; and maybe more." : "."));
        }

        ContainerUpdateResult result = await comparison.UpdateAsync(cancellationToken).ConfigureAwait(false);
        if (result is { Imported: 0, Changed: 0, Deleted: 0 } && !comparison.MetadataChanged)
        {
            matching.Add(container.Name);
            return ContainerCopyOutcome.Matching;
        }
        updated.Add(result);
        return ContainerCopyOutcome.Updated;
    }

    private static ResourceConflictException Conflict(string containerName, string message) =>
        new(message) { ContainerName = containerName };

    /// <summary>
    /// One container in this store, compared with the destination's container of the same name, name by name: this
    /// store's names a page at a time, then the destination's names this store doesn't have.
    /// </summary>
    private sealed class ContainerComparison(ResourceServiceClient source, ResourceRepository repository, ContainerInfo container,
        ContainerInfo theirs, IResourceServiceClient destination)
    {
        private readonly IResourceContainerClient _destination = destination.GetContainerClient(container.Name);

        /// <summary>Whether <see cref="UpdateAsync"/> gave the destination's container the source's metadata.</summary>
        public bool MetadataChanged { get; private set; }

        /// <summary>Up to <see cref="DifferencesNamed"/> differences, described; none when the containers match.</summary>
        public async Task<List<string>> FindDifferencesAsync(CancellationToken cancellationToken)
        {
            var differences = new List<string>();
            if (!SameMetadata(WithoutImportMark(container.Metadata), theirs.Metadata)) differences.Add("the container's metadata differs");
            await foreach (NamePlan plan in PlansAsync(cancellationToken).ConfigureAwait(false))
            {
                differences.AddRange(plan.Differences);
                if (differences.Count >= DifferencesNamed) return differences[..DifferencesNamed];
            }
            return differences;
        }

        /// <summary>
        /// Brings the destination's container up to date, a name at a time, refusing a name it can't change in place
        /// before changing anything of that name.
        /// </summary>
        /// <exception cref="ResourceConflictException">A name needs what an update can't do, such as an older version put back.</exception>
        public async Task<ContainerUpdateResult> UpdateAsync(CancellationToken cancellationToken)
        {
            long imported = 0, changed = 0, deleted = 0;
            var missing = new List<ImportedVersion>();
            async Task ImportMissingAsync()
            {
                if (missing.Count == 0) return;
                imported += (await destination.ImportVersionsAsync(container.Name, missing.ToAsyncEnumerable(), cancellationToken)
                    .ConfigureAwait(false)).VersionCount;
                missing.Clear();
            }

            await foreach (NamePlan plan in PlansAsync(cancellationToken).ConfigureAwait(false))
            {
                if (plan.Unchangeable is { } reason)
                    throw Conflict(container.Name, $"The destination's container {container.Name} can't be updated in place: {reason}. Copy it afresh.");
                foreach (ResourceInfo extra in plan.Extras)
                {
                    await _destination.DeleteResourceIfExistsAsync(extra.Name, extra.VersionId, null, cancellationToken).ConfigureAwait(false);
                    deleted++;
                }
                foreach ((ResourceInfo ours, ResourceInfo their) in plan.Changed)
                {
                    await ChangeAsync(ours, their, cancellationToken).ConfigureAwait(false);
                    changed++;
                }
                missing.AddRange(plan.Missing.Select(v => source.ToImport(container.Name, v)));
                if (missing.Count >= ContainerArchive.NamesPerPage) await ImportMissingAsync().ConfigureAwait(false);
            }
            await ImportMissingAsync().ConfigureAwait(false);

            // Last, so the mark of an import that stopped partway stays until the container is whole.
            Dictionary<string, string> metadata = WithoutImportMark(container.Metadata);
            if (!SameMetadata(metadata, theirs.Metadata))
            {
                await destination.GetContainerClient(container.Name).SetMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
                MetadataChanged = true;
            }

            source._telemetry.CountCopied("imported", imported);
            source._telemetry.CountCopied("changed", changed);
            source._telemetry.CountCopied("deleted", deleted);
            return new ContainerUpdateResult(container.Name, imported, changed, deleted);
        }

        /// <summary>Gives the destination's version what differs in the source's, of what can change in place.</summary>
        private async Task ChangeAsync(ResourceInfo ours, ResourceInfo their, CancellationToken cancellationToken)
        {
            string name = ours.Name, versionId = ours.VersionId;
            if (ours.ContentType != their.ContentType)
                await _destination.SetContentTypeAsync(name, versionId, ours.ContentType, cancellationToken).ConfigureAwait(false);
            if (!SameMetadata(ours.Metadata, their.Metadata))
                await _destination.SetResourceMetadataAsync(name, versionId, new Dictionary<string, string>(ours.Metadata), cancellationToken).ConfigureAwait(false);
            if (Micro(ours.ExpiresAt) != Micro(their.ExpiresAt))
                await _destination.SetExpiryAsync(name, versionId, ours.ExpiresAt, cancellationToken).ConfigureAwait(false);
            if (Micro(ours.RetainUntil) != Micro(their.RetainUntil))
                await _destination.SetRetentionAsync(name, versionId, ours.RetainUntil!.Value, cancellationToken).ConfigureAwait(false);
            if (ours.LegalHold != their.LegalHold)
                await _destination.SetLegalHoldAsync(name, versionId, ours.LegalHold, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>How each name differs: this store's names, then the destination's names this store doesn't have.</summary>
        private async IAsyncEnumerable<NamePlan> PlansAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (IReadOnlyList<ResourceInfo> page in source.VersionPagesAsync(container.Name, cancellationToken).ConfigureAwait(false))
            {
                foreach (IGrouping<string, ResourceInfo> name in page.GroupBy(v => v.Name, StringComparer.Ordinal))
                {
                    IReadOnlyList<ResourceInfo> theirVersions = await _destination.GetResourceVersionsAsync(name.Key, cancellationToken).ConfigureAwait(false);
                    yield return NamePlan.Of(name.Key, [.. name.Reverse()], [.. theirVersions.Reverse()]);
                }
            }
            await foreach (ResourceInfo resource in _destination.ListResourcesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (await repository.GetResourceInfoAsync(new ResourceAddress(container.Name, resource.Name), null,
                    cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }
                IReadOnlyList<ResourceInfo> theirVersions = await _destination.GetResourceVersionsAsync(resource.Name, cancellationToken).ConfigureAwait(false);
                yield return NamePlan.Of(resource.Name, [], [.. theirVersions.Reverse()]);
            }
        }
    }

    /// <summary>
    /// How one name's versions in the destination differ from the source's, both oldest first: the versions only the
    /// destination has, those whose attributes that can change in place differ, those only the source has, and why the
    /// name can't be brought up to date in place, if it can't.
    /// </summary>
    private sealed record NamePlan(List<string> Differences, List<ResourceInfo> Extras, List<(ResourceInfo Ours, ResourceInfo Theirs)> Changed,
        List<ResourceInfo> Missing, string? Unchangeable)
    {
        public static NamePlan Of(string name, List<ResourceInfo> ours, List<ResourceInfo> theirs)
        {
            var theirById = theirs.ToDictionary(v => v.VersionId, StringComparer.Ordinal);
            var ourIds = ours.Select(v => v.VersionId).ToHashSet(StringComparer.Ordinal);
            var differences = new List<string>();
            List<ResourceInfo> extras = [.. theirs.Where(v => !ourIds.Contains(v.VersionId))];
            List<ResourceInfo> missing = [.. ours.Where(v => !theirById.ContainsKey(v.VersionId))];
            var changed = new List<(ResourceInfo, ResourceInfo)>();
            string? unchangeable = null;
            if (ours.Count == 0) differences.Add($"{name} isn't in the source");
            else if (theirs.Count == 0) differences.Add($"{name} isn't in the destination");
            else
            {
                foreach (ResourceInfo extra in extras) differences.Add($"{name} version {extra.VersionId} isn't in the source");
                foreach (ResourceInfo absent in missing) differences.Add($"{name} version {absent.VersionId} isn't in the destination");
            }

            foreach (ResourceInfo version in ours.Where(v => theirById.ContainsKey(v.VersionId)))
            {
                ResourceInfo their = theirById[version.VersionId];
                if (ContentDiffers(version, their))
                {
                    differences.Add($"{name} version {version.VersionId} has other content, origin or time");
                    unchangeable ??= $"{name} version {version.VersionId} has other content, origin or time";
                }
                else if (their.RetainUntil is { } theirDate && !(Micro(version.RetainUntil) >= Micro(theirDate)))
                {
                    differences.Add($"{name} version {version.VersionId} is retained longer");
                    unchangeable ??= $"{name} version {version.VersionId} is retained longer than the source's, and that can't move earlier";
                }
                else if (version.ContentType != their.ContentType || !SameMetadata(version.Metadata, their.Metadata)
                    || Micro(version.ExpiresAt) != Micro(their.ExpiresAt) || Micro(version.RetainUntil) != Micro(their.RetainUntil)
                    || version.LegalHold != their.LegalHold)
                {
                    differences.Add($"{name} version {version.VersionId} has other attributes");
                    changed.Add((version, their));
                }
            }

            // The versions both have must be in the same order, and those the destination lacks come after them, since an
            // import adds each version as the newest.
            List<string> ourCommon = [.. ours.Where(v => theirById.ContainsKey(v.VersionId)).Select(v => v.VersionId)];
            List<string> theirCommon = [.. theirs.Where(v => ourIds.Contains(v.VersionId)).Select(v => v.VersionId)];
            if (!ourCommon.SequenceEqual(theirCommon, StringComparer.Ordinal))
            {
                differences.Add($"{name}'s versions are in another order");
                unchangeable ??= $"{name}'s versions are in another order";
            }
            else if (ourCommon.Count > 0
                && missing.Any(v => ours.IndexOf(v) < ours.FindIndex(o => o.VersionId == ourCommon[^1])))
            {
                unchangeable ??= $"{name} in the destination lacks a version older than ones it has";
            }
            return new NamePlan(differences, extras, changed, missing, unchangeable);
        }

        /// <summary>
        /// Whether two versions with the same ID differ in what can't change in place: content, LastModified or
        /// CopiedFrom. Content is compared by hash when both have one, since a version stored before lengths were
        /// measured may record a length its content doesn't have, and by length otherwise.
        /// </summary>
        private static bool ContentDiffers(ResourceInfo ours, ResourceInfo theirs) =>
            (ours.ContentHash is not null && theirs.ContentHash is not null
                ? ours.ContentHash != theirs.ContentHash
                : ours.ContentLength is not null && theirs.ContentLength is not null && ours.ContentLength != theirs.ContentLength)
            || Micro(ours.LastModified) != Micro(theirs.LastModified)
            || ours.CopiedFrom != theirs.CopiedFrom;
    }

    private static bool SameMetadata(IReadOnlyDictionary<string, string> ours, IReadOnlyDictionary<string, string> theirs)
        => MetadataDictionary.From(ours).Equals(MetadataDictionary.From(theirs));

    /// <summary>A time cut to the microsecond, the finest every provider keeps, for comparing stores.</summary>
    private static DateTimeOffset? Micro(DateTimeOffset? time) =>
        time is { } value ? new DateTimeOffset(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero) : null;
}
