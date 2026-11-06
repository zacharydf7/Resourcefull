using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Resourcefull.Providers;

namespace Resourcefull;

internal partial class ResourceContainerClient(ResourceRepository resourceRepository, IResourceUriProvider? uriProvider,
    ResourcefullOptions options, string containerName, ILogger? logger, ContainerPolicies? policies = null,
    IResourceCleanupHandler? cleanupHandler = null, TimeProvider? timeProvider = null, ExpirySettings? expiry = null,
    IResourceUploadInspector? uploadInspector = null)
    : IResourceContainerClient
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly ClientTelemetry _telemetry = new(Telemetry.ProviderName(resourceRepository), options);

    public string Name { get; } = containerName;

    private string ContainerName => Name;

    // The store's options, for methods whose own options parameter hides the primary constructor's.
    private ResourcefullOptions StoreOptions => options;

    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? options = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("upload", ContainerName, resourceName, activity =>
        {
            activity?.SetTag("resourcefull.write_mode", WriteModeTag(options?.Mode ?? ResourceWriteMode.Create));
            return UploadCoreAsync(resourceName, content, options, cancellationToken);
        }, "resourcefull.conditional", options?.Conditions is { IsEmpty: false });

    private async Task<ResourceInfo> UploadCoreAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ResourceNames.ThrowIfInvalidNewResourceName(resourceName);

        uploadOptions ??= new ResourceUploadOptions();
        options.ThrowIfUnsearchable(uploadOptions.Metadata.AsReadOnly(), "options");
        ResourceConditions? conditions = uploadOptions.Conditions is { IsEmpty: false } set ? set : null;
        bool createOnly = IsCreate(uploadOptions.Mode, conditions, nameof(ResourceUploadOptions.Conditions), "options");
        NewVersionCheck check = policies is null ? default : await policies.CheckNewVersionAsync(ContainerName, resourceName, restore: false,
            uploadOptions.Headers?.ContentType, uploadOptions.Headers?.ContentLength, _telemetry, cancellationToken).ConfigureAwait(false);
        if (check.Limit is { } limit) content = new LimitedContentStream(content, limit);

        var version = new NewResourceVersion(At(resourceName), Guid.CreateVersion7().ToString())
        {
            ContentType = uploadOptions.Headers?.ContentType,
            DeclaredLength = uploadOptions.Headers?.ContentLength,
            Metadata = new Dictionary<string, string>(uploadOptions.Metadata),
            ExpiresAt = ExpiryToStore(uploadOptions.ExpiresAt),
            RetainUntil = RetainUntil(check.RetainFor, uploadOptions.RetainUntil, uploadOptions.LegalHold),
            LegalHold = uploadOptions.LegalHold
        };

        // Disposed in a finally rather than with await using, since ConfigureAwait's wrapper can't dispose null.
        InspectedContentStream? inspected = uploadInspector is null
            ? null
            : InspectedContentStream.Start(uploadInspector, version, version.DeclaredLength, content, _telemetry, "upload",
                cancellationToken);
        try
        {
            return await SaveNewResourceVersionAsync("upload", inspected ?? content, version, createOnly, conditions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceContentLengthMismatchException exception)
        {
            throw new ResourceContentLengthMismatchException(
                $"The resource {resourceName} wasn't stored in the container {ContainerName}: {exception.Message}",
                exception)
            {
                ContainerName = ContainerName, ResourceName = resourceName
            };
        }
        catch (ResourceConflictException exception)
        {
            throw new ResourceConflictException(
                $"The resource {resourceName} already exists in the container {ContainerName}. Set " +
                $"{nameof(ResourceUploadOptions.Mode)} to {nameof(ResourceWriteMode.Replace)} in {nameof(ResourceUploadOptions)} to replace it.",
                exception)
            {
                ContainerName = ContainerName, ResourceName = resourceName
            };
        }
        finally
        {
            if (inspected is not null) await inspected.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<ResourceInfo> UploadAsync(string resourceName, BinaryData content, ResourceUploadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return UploadAsync(resourceName, content.ToStream(), options, cancellationToken);
    }

    public Task<ResourceInfo?> RestoreVersionAsync(string resourceName, string versionId,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("restore", ContainerName, resourceName, () => RestoreCoreAsync(resourceName, versionId, cancellationToken));

    private async Task<ResourceInfo?> RestoreCoreAsync(string resourceName, string versionId, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        if (string.IsNullOrEmpty(versionId))
            throw new ArgumentException($"{nameof(versionId)} can't be empty.", nameof(versionId));

        ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(At(resourceName), versionId, cancellationToken).ConfigureAwait(false);
        if (resourceInfo == null) return null;
        options.ThrowIfUnsearchable(resourceInfo.Metadata, nameof(versionId));
        // As for a copy, the stored length stands in for a declared one: the content doesn't pass through here.
        NewVersionCheck check = default;
        if (policies is not null)
        {
            resourceInfo = await WithStoredLengthAsync(resourceInfo, cancellationToken).ConfigureAwait(false);
            if (resourceInfo is null) return null;
            check = await policies.CheckNewVersionAsync(ContainerName, resourceName, restore: true, null,
                resourceInfo.ContentLength, _telemetry, cancellationToken).ConfigureAwait(false);
        }

        // Copied within the store, as a copy is, so a provider that copies on the server can retry a transient failure
        // rather than end the restore, which it can't do for content streamed through here.
        var restored = new NewResourceVersion(At(resourceName), Guid.CreateVersion7().ToString())
        {
            ContentType = resourceInfo.ContentType,
            Metadata = resourceInfo.Metadata,
            CopiedFrom = new ResourceOrigin(ContainerName, resourceName, versionId),
            RetainUntil = RetainUntil(check.RetainFor, null, false)
        };
        WrittenContent? written = await resourceRepository.CopyResourceAsync(At(resourceName), versionId, restored, null,
            cancellationToken).ConfigureAwait(false);
        if (written is null) return null;

        _telemetry.RecordUploaded("restore", written.ContentLength);
        return await CompleteNewVersionAsync(restored, written).ConfigureAwait(false);
    }

    private async Task<ResourceInfo> SaveNewResourceVersionAsync(string operation, Stream stream, NewResourceVersion version,
        bool createOnly, ResourceConditions? conditions, CancellationToken cancellationToken)
    {
        WrittenContent written = await resourceRepository.AddResourceAsync(version, stream,
            new ResourceWriteCondition { CreateOnly = createOnly, Conditions = conditions }, cancellationToken).ConfigureAwait(false);
        if (stream is InspectedContentStream inspected) await DeleteIfRefusedAsync(inspected, version, cancellationToken).ConfigureAwait(false);

        _telemetry.RecordUploaded(operation, written.ContentLength);
        return await CompleteNewVersionAsync(version, written).ConfigureAwait(false);
    }

    /// <summary>
    /// If the upload inspector refuses the content or fails, deletes the version the provider made current before the
    /// inspector answered, as a provider that doesn't read the content to its end does.
    /// </summary>
    private async Task DeleteIfRefusedAsync(InspectedContentStream inspected, NewResourceVersion version,
        CancellationToken cancellationToken)
    {
        try
        {
            await inspected.ThrowIfStoredUnansweredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await resourceRepository.DeleteResourceAsync(version.Resource, version.VersionId, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Describes a new version as it was stored, and prunes its name's older versions in whichever container it's in.
    /// </summary>
    private async Task<ResourceInfo> CompleteNewVersionAsync(NewResourceVersion version, WrittenContent written)
    {
        (string containerName, string resourceName) = version.Resource;
        // Report what was stored rather than what was asked for.
        ResourceInfo stored = WithUri(new ResourceInfo
        {
            ContainerName = containerName,
            Name = resourceName,
            VersionId = version.VersionId,
            ContentType = version.ContentType,
            Metadata = version.Metadata,
            CopiedFrom = version.CopiedFrom,
            ExpiresAt = version.ExpiresAt,
            RetainUntil = version.RetainUntil,
            LegalHold = version.LegalHold,
            ContentLength = written.ContentLength,
            ContentHash = written.ContentHash,
            LastModified = written.LastModified,
            IsLatest = true
        });

        // The new version is stored, so the upload has succeeded.
        await PruneAfterAsync(containerName, resourceName, async () =>
            await PruneAsync(containerName, resourceName, await MaxVersionsAsync(containerName, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        return stored;
    }

    /// <summary>
    /// Runs a prune after a write that succeeded, as cleanup that neither the caller's token nor a failure may turn
    /// into a reported failure.
    /// </summary>
    private async Task PruneAfterAsync(string containerName, string resourceName, Func<Task> prune)
    {
        using Activity? activity = _telemetry.StartSpan("prune", containerName);
        try
        {
            await prune().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _telemetry.RecordFailure(activity, exception);
            _telemetry.CountFailure(Telemetry.PruneFailures, exception);
            // The older versions stay until a later write of the name prunes them, or they expire; every prune works
            // from the versions as they stand after its own write. Logged, as a prune that keeps failing would
            // otherwise let storage grow unnoticed.
            try
            {
                LogPruneFailed(_logger, exception, containerName, resourceName);
            }
            catch
            {
                // A logging provider that throws mustn't fail a write that succeeded either.
            }
        }
    }

    /// <summary>
    /// Keeps the newest versions, up to the container's limit, in store order. Reading the versions after the write means
    /// concurrent writers each see all of them, so together they never keep more than the limit; with a limit of one,
    /// that's what a replacing upload leaves.
    /// </summary>
    /// <returns>How many versions were past the limit.</returns>
    private async Task<int> PruneAsync(string containerName, string resourceName, int keep, CancellationToken cancellationToken)
    {
        IReadOnlyList<ResourceInfo> versions = await resourceRepository.GetResourceVersionsAsync(
            new ResourceAddress(containerName, resourceName), cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DateTimeOffset? purgeAt = versions.Count > keep ? await PurgeAtAsync(containerName, now, cancellationToken).ConfigureAwait(false) : null;
        foreach (ResourceInfo version in versions.Skip(keep))
        {
            // A retained or held version stays past the limit until it's no longer protected; then the next prune removes it.
            if (!version.IsDeletableAt(now)) continue;

            // With expiry on, a version past the limit stays a few minutes more, so a download reading it can finish; the
            // sweep deletes it, telling the cleanup handler then. A provider without expiry deletes it now. With a bin,
            // it goes there at once instead, since its content stays readable there.
            if (purgeAt is null && expiry is not null && resourceRepository.SupportsExpiry)
            {
                await ExpireByAsync(containerName, version, now + PruneGracePeriod, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await DeletePrunedAsync(version, now, cancellationToken).ConfigureAwait(false);
        }

        return Math.Max(0, versions.Count - keep);
    }

    /// <summary>
    /// Prunes every name in the container, a page of names at a time, as each name's own writes would. A read-only
    /// container is refused, as any change to it is.
    /// </summary>
    /// <returns>How many versions were past the limit, in the names pruned.</returns>
    /// <exception cref="AggregateException">Some names weren't pruned; the others were.</exception>
    private async Task<long> PruneAllAsync(Activity? activity, CancellationToken cancellationToken)
    {
        if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
        int keep = await MaxVersionsAsync(ContainerName, cancellationToken).ConfigureAwait(false);
        long pruned = 0, failed = 0;
        // The first few failures are enough to say why; an outage of the cleanup handler's dependency fails every name.
        var failures = new List<Exception>();
        string? afterName = null;
        while (true)
        {
            IReadOnlyList<ResourceInfo> page = await resourceRepository.GetResourcePageAsync(ContainerName, null, afterName,
                ResourceListOptions.DefaultPageSize, cancellationToken).ConfigureAwait(false);
            foreach (ResourceInfo resource in page)
            {
                try
                {
                    pruned += await PruneAsync(ContainerName, resource.Name, keep, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // One name that keeps failing, such as one the cleanup handler refuses, mustn't hold up the rest.
                    failed++;
                    _telemetry.CountFailure(Telemetry.PruneFailures, exception);
                    if (failures.Count < ResourceServiceClient.FailuresKept) failures.Add(exception);
                }
            }

            if (page.Count < ResourceListOptions.DefaultPageSize) break;
            afterName = page[^1].Name;
        }

        activity?.SetTag("resourcefull.pruned", pruned);
        if (failed > 0)
        {
            throw new AggregateException($"{failed} resources weren't pruned, and keep their extra versions until a later prune; " +
                $"{pruned} versions past the limit were pruned in the others. The first {failures.Count} failures follow.", failures);
        }

        return pruned;
    }

    /// <summary>
    /// Deletes a pruned version, telling the cleanup handler first, unless it's retained or held: then it stays for a
    /// later prune.
    /// </summary>
    private async Task DeletePrunedAsync(ResourceInfo version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!version.IsDeletableAt(now)) return;
        // Told first, so a handler that fails leaves the version for a later prune rather than deleted unreported.
        if (cleanupHandler is not null)
            await _telemetry.NotifyCleanupAsync(cleanupHandler, new ResourceCleanup
            {
                Version = version,
                Reason = ResourceCleanupReason.Pruned,
                At = now
            }, cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteAsync(new ResourceAddress(version.ContainerName, version.Name), version.VersionId, null,
                await PurgeAtAsync(version.ContainerName, now, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceRetainedException refusal)
        {
            // A hold or a later date set since the versions were read keeps it.
            _telemetry.CountRefusal("prune", refusal);
        }
    }

    /// <summary>
    /// In a container that keeps one version, the replaced versions still behind <paramref name="currentVersionId"/>:
    /// those waiting out their grace period, and those retained or held. Deleting the current version must take them
    /// too, or the most recent of them would become current again, bringing back content the upload replaced. Empty in
    /// a container that keeps more, where an older version becoming current is what deleting the current one does.
    /// </summary>
    internal async Task<IReadOnlyList<ResourceInfo>> WaitingBehindAsync(string resourceName, string currentVersionId,
        CancellationToken cancellationToken)
    {
        if (await MaxVersionsAsync(ContainerName, cancellationToken).ConfigureAwait(false) > 1) return [];
        IReadOnlyList<ResourceInfo> versions = await resourceRepository.GetResourceVersionsAsync(At(resourceName), cancellationToken).ConfigureAwait(false);
        return versions.Count > 0 && versions[0].VersionId == currentVersionId ? [.. versions.Skip(1)] : [];
    }

    /// <summary>
    /// The first of the versions <see cref="WaitingBehindAsync"/> found that can't be deleted at <paramref name="now"/>,
    /// which keeps the current version from being deleted: it would become current again.
    /// </summary>
    internal static ResourceInfo? Retained(IReadOnlyList<ResourceInfo> waiting, DateTimeOffset now) =>
        waiting.FirstOrDefault(v => !v.IsDeletableAt(now));

    /// <summary>Deletes the versions <see cref="WaitingBehindAsync"/> found, once the current one is deleted.</summary>
    internal Task DeleteWaitingAsync(string resourceName, IReadOnlyList<ResourceInfo> waiting) =>
        waiting.Count == 0
            ? Task.CompletedTask
            : PruneAfterAsync(ContainerName, resourceName, async () =>
            {
                DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
                foreach (ResourceInfo version in waiting) await DeletePrunedAsync(version, now, CancellationToken.None).ConfigureAwait(false);
            });

    /// <summary>
    /// When a version deleted from <paramref name="containerName"/> at <paramref name="now"/> leaves the bin: its
    /// policy's <see cref="ContainerPolicy.KeepDeletedFor"/> from now, or <see langword="null"/> when it has
    /// none, and deletes are permanent.
    /// </summary>
    /// <exception cref="NotSupportedException">The container keeps deleted versions, and the provider can't.</exception>
    /// <exception cref="InvalidOperationException">The container keeps deleted versions, and nothing would purge them.</exception>
    private async ValueTask<DateTimeOffset?> PurgeAtAsync(string containerName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (policies is null
            || (await policies.GetAsync(containerName, _telemetry, cancellationToken).ConfigureAwait(false)).KeepDeletedFor is not { } keep
            || keep <= TimeSpan.Zero)
            return null;
        if (!resourceRepository.SupportsSoftDelete)
            throw new NotSupportedException($"{resourceRepository.GetType().Name} doesn't keep deleted versions, which the container {containerName}'s policy asks for.");
        if (expiry is null)
        {
            throw new InvalidOperationException($"The container {containerName}'s policy keeps deleted versions, but nothing would purge them: " +
                $"turn on {nameof(ResourcefullOptionsBuilder.UseExpiry)}, whose sweep does.");
        }
        return now + keep;
    }

    /// <summary>Deletes into the bin, until <paramref name="purgeAt"/>, or for good when that's <see langword="null"/>.</summary>
    private Task<bool> DeleteAsync(ResourceAddress resource, string? versionId, ResourceConditions? conditions, DateTimeOffset? purgeAt,
        CancellationToken cancellationToken) =>
        purgeAt is { } at
            ? resourceRepository.SoftDeleteResourceAsync(resource, versionId, conditions, at, cancellationToken)
            : resourceRepository.DeleteResourceAsync(resource, versionId, conditions, cancellationToken);

    /// <summary>
    /// How many versions of each name <paramref name="containerName"/> keeps: its policy's, or the store-wide default.
    /// </summary>
    private async ValueTask<int> MaxVersionsAsync(string containerName, CancellationToken cancellationToken) =>
        (policies is null ? null : (await policies.GetAsync(containerName, _telemetry, cancellationToken).ConfigureAwait(false)).MaxVersions)
            ?? options.MaxVersions;

    /// <summary>
    /// The <see cref="NewResourceVersion.RetainUntil"/> of a new version: the one its write asks for, or else
    /// <paramref name="retainFor"/>, its container's <see cref="ContainerPolicy.RetainFor"/>, from now. A
    /// provider that can't keep it is refused, so nothing is stored unprotected.
    /// </summary>
    /// <exception cref="NotSupportedException">The version would be retained or held, and the provider doesn't support retention.</exception>
    private DateTimeOffset? RetainUntil(TimeSpan? retainFor, DateTimeOffset? retainUntil, bool legalHold)
    {
        if (retainUntil is null && retainFor > TimeSpan.Zero) retainUntil = (timeProvider ?? TimeProvider.System).GetUtcNow() + retainFor;
        if ((retainUntil is not null || legalHold) && !resourceRepository.SupportsRetention)
            throw new NotSupportedException($"{resourceRepository.GetType().Name} doesn't support retention.");
        return retainUntil;
    }

    /// <summary>
    /// The <see cref="NewResourceVersion.ExpiresAt"/> of a new version. A provider that can't act on one is refused, so
    /// nothing is stored with an expiry that never happens.
    /// </summary>
    /// <exception cref="NotSupportedException">The version would expire, and the provider doesn't support expiry.</exception>
    private DateTimeOffset? ExpiryToStore(DateTimeOffset? expiresAt)
    {
        if (expiresAt is not null && !resourceRepository.SupportsExpiry)
            throw new NotSupportedException($"{resourceRepository.GetType().Name} doesn't support expiry.");
        return expiresAt;
    }

    /// <summary>
    /// Whether a write in <paramref name="mode"/> is create-only. Throws for an undefined mode, or for conditions with
    /// <see cref="ResourceWriteMode.Create"/>: a name that meets them has a current version, which a create-only write
    /// refuses.
    /// </summary>
    private static bool IsCreate(ResourceWriteMode mode, ResourceConditions? conditions, string conditionsName, string paramName)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(paramName, mode, $"{mode} isn't a {nameof(ResourceWriteMode)}.");
        if (mode == ResourceWriteMode.Create && conditions is not null)
            throw new ArgumentException(
                $"{conditionsName} need {nameof(ResourceWriteMode)}.{nameof(ResourceWriteMode.Replace)}: a name that meets them " +
                $"has a current version, which {nameof(ResourceWriteMode)}.{nameof(ResourceWriteMode.Create)} refuses.", paramName);
        return mode == ResourceWriteMode.Create;
    }

    /// <summary>The <c>resourcefull.write_mode</c> span tag's value for <paramref name="mode"/>.</summary>
    private static string WriteModeTag(ResourceWriteMode mode) => mode == ResourceWriteMode.Create ? "create" : "replace";

    /// <summary>How long a version past the versioning limit is kept when expiry is on, for downloads reading it.</summary>
    internal static readonly TimeSpan PruneGracePeriod = TimeSpan.FromMinutes(5);

    /// <summary>Sets a pruned version to expire at <paramref name="expiresAt"/>, unless it already expires sooner.</summary>
    private async Task ExpireByAsync(string containerName, ResourceInfo version, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        if (version.ExpiresAt <= expiresAt) return;
        await resourceRepository.SetExpiryAsync(new ResourceAddress(containerName, version.Name), version.VersionId, expiresAt,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId = null, ResourceRange? range = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("download", ContainerName, resourceName, () => OpenAsync(resourceName, versionId, range, cancellationToken),
            range is null ? null : "resourcefull.range", range is { } part ? $"{part.Offset}-{part.Offset + part.Length - 1}" : null);

    private async Task<ResourceDownloadResult?> OpenAsync(string resourceName, string? versionId, ResourceRange? range,
        CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        if (range is { } requested && (requested.Offset < 0 || requested.Length < 0))
            throw new ArgumentOutOfRangeException(nameof(range), requested, "A range can't have a negative offset or length.");

        ResourceInfo? resolved = await resourceRepository.GetResourceInfoAsync(At(resourceName), versionId, cancellationToken).ConfigureAwait(false);
        if (resolved == null) return null;
        // Before the stream opens, so a URI provider that throws can't leave it undisposed.
        ResourceInfo resourceInfo = WithUri(resolved);

        // Read the version just resolved, not "current" again: an upload in between would pair this info with other content.
        Stream? stream = range is { } r
            ? await resourceRepository.GetResourceStreamAsync(At(resourceName), resourceInfo.VersionId, r.Offset, r.Length, cancellationToken).ConfigureAwait(false)
            : await resourceRepository.GetResourceStreamAsync(At(resourceName), resourceInfo.VersionId, cancellationToken).ConfigureAwait(false);
        if (stream == null) return null;

        long totalLength = stream.Length;
        if (range is not { } part)
            return new ResourceDownloadResult(resourceInfo, _telemetry.CountDownloaded(stream), totalLength);

        if (part.Offset > totalLength)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new ResourceRangeNotSatisfiableException(
                $"The range starts at byte {part.Offset}, past the end of the resource {resourceName}, which is " +
                $"{totalLength} bytes long.",
                totalLength) { ContainerName = ContainerName, ResourceName = resourceName };
        }

        long length = Math.Min(part.Length ?? long.MaxValue, totalLength - part.Offset);
        return new ResourceDownloadResult(resourceInfo, _telemetry.CountDownloaded(new ResourceRangeStream(stream, length)), totalLength)
        {
            Range = new ResourceRange(part.Offset, length)
        };
    }

    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("download", ContainerName, resourceName, async () =>
        {
            ResourceDownloadResult? result = await OpenAsync(resourceName, versionId, null, cancellationToken).ConfigureAwait(false);
            if (result is null) return null;

            Stream stream = result.Stream;
            await using var streamDisposal = stream.ConfigureAwait(false);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            return new ResourceContent
            {
                ResourceInfo = result.ResourceInfo,
                Content = BinaryData.FromBytes(memoryStream.ToArray())
            };
        });

    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("get_info", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            ResourceInfo? resourceInfo = await resourceRepository.GetResourceInfoAsync(At(resourceName), versionId, cancellationToken).ConfigureAwait(false);
            return resourceInfo is null ? null : WithUri(resourceInfo);
        });

    public Task<ResourcePage> GetResourcePageAsync(ResourceListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("list_page", ContainerName, null, activity =>
        {
            // The key, never the value, which may be personal data.
            if (options?.MetadataMatch is { } match) activity?.SetTag("resourcefull.metadata_key", match.Key);
            return GetPageCoreAsync(options, continuationToken, cancellationToken);
        }, "resourcefull.page_size", options?.PageSize ?? ResourceListOptions.DefaultPageSize);

    private async Task<ResourcePage> GetPageCoreAsync(ResourceListOptions? options, string? continuationToken, CancellationToken cancellationToken)
    {
        options ??= new ResourceListOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PageSize, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PageSize, ResourceListOptions.MaxPageSize, nameof(options));
        string? prefix = string.IsNullOrEmpty(options.Prefix) ? null : options.Prefix;
        ResourceMetadataMatch? match = options.MetadataMatch;
        if (match is not null)
        {
            ArgumentNullException.ThrowIfNull(match.Key, nameof(options));
            ArgumentNullException.ThrowIfNull(match.Value, nameof(options));
            if (!StoreOptions.SearchableMetadataKeys.Contains(match.Key))
            {
                throw new ArgumentException($"The metadata key {match.Key} can't be searched: name it with " +
                    $"{nameof(ResourcefullOptionsBuilder.UseMetadataSearch)} to search by it.", nameof(options));
            }
            if (match.Value.Length > ResourceMetadataMatch.MaxValueLength)
            {
                throw new ArgumentException($"A search can look for a value of at most {ResourceMetadataMatch.MaxValueLength} " +
                    "characters, as long as a searchable key's value can be.", nameof(options));
            }
        }
        string? afterName = continuationToken is null ? null : ResourcePageToken.Decode(continuationToken, prefix, nameof(continuationToken), match);

        // One more than a page, to tell whether another page follows without returning an empty last page.
        IReadOnlyList<ResourceInfo> resources = match is null
            ? await resourceRepository.GetResourcePageAsync(ContainerName, prefix, afterName, options.PageSize + 1, cancellationToken).ConfigureAwait(false)
            : await resourceRepository.FindResourcePageAsync(ContainerName, prefix, afterName, options.PageSize + 1, match, cancellationToken).ConfigureAwait(false);
        string? next = null;
        if (resources.Count > options.PageSize)
        {
            resources = [.. resources.Take(options.PageSize)];
            next = ResourcePageToken.Encode(prefix, resources[^1].Name, match);
        }
        return new ResourcePage(WithUris(resources), next);
    }

    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync<IReadOnlyList<ResourceInfo>>("list_versions", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            return WithUris(await resourceRepository.GetResourceVersionsAsync(At(resourceName), cancellationToken).ConfigureAwait(false));
        });

    // URIs come from the URI provider whenever a version is returned, not from storage, so a changed provider or base URL
    // applies to versions stored before the change as well.
    private ResourceInfo WithUri(ResourceInfo resourceInfo) => resourceInfo with
    {
        Uri = uriProvider?.GetAbsoluteUri(new ResourceUriContext(resourceInfo.ContainerName, resourceInfo.Name, resourceInfo.VersionId))
    };

    private IReadOnlyList<ResourceInfo> WithUris(IReadOnlyList<ResourceInfo> resources) => [.. resources.Select(WithUri)];

    private ResourceAddress At(string resourceName) => new(ContainerName, resourceName);

    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null, ResourceConditions? conditions = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("delete", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ResourceInfo> waiting = versionId is null ? [] : await WaitingBehindAsync(resourceName, versionId, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            if (Retained(waiting, now) is { } retained)
                throw ResourceRetainedException.Create(At(resourceName), retained.VersionId, retained.RetainUntil, retained.LegalHold, now);
            bool deleted = await DeleteAsync(At(resourceName), versionId, conditions is { IsEmpty: false } ? conditions : null,
                await PurgeAtAsync(ContainerName, now, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (deleted) await DeleteWaitingAsync(resourceName, waiting).ConfigureAwait(false);
            return deleted;
        }, "resourcefull.conditional", conditions is { IsEmpty: false });

    public Task<ResourceInfo?> CopyResourceAsync(string resourceName, string destinationContainerName, string destinationName,
        ResourceCopyOptions? options = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("copy", ContainerName, resourceName, activity =>
        {
            activity?.SetTag("resourcefull.write_mode", WriteModeTag(options?.Mode ?? ResourceWriteMode.Create));
            return CopyCoreAsync(resourceName, destinationContainerName, destinationName, options ?? new ResourceCopyOptions(), cancellationToken);
        }, "resourcefull.destination_container", destinationContainerName);

    private async Task<ResourceInfo?> CopyCoreAsync(string resourceName, string destinationContainerName, string destinationName,
        ResourceCopyOptions copyOptions, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        ResourceNames.ThrowIfInvalidContainerName(destinationContainerName);
        ResourceNames.ThrowIfInvalidNewResourceName(destinationName);
        ResourceConditions? conditions = copyOptions.DestinationConditions is { IsEmpty: false } set ? set : null;
        // "options" is CopyResourceAsync's parameter; nameof(options) here is the client's own options.
        bool createOnly = IsCreate(copyOptions.Mode, conditions, nameof(ResourceCopyOptions.DestinationConditions), "options");

        ResourceInfo? source = await resourceRepository.GetResourceInfoAsync(At(resourceName), copyOptions.VersionId, cancellationToken).ConfigureAwait(false);
        if (source is null) return null;
        options.ThrowIfUnsearchable(source.Metadata, nameof(resourceName));
        // The stored length stands in for a declared one: the content doesn't pass through here to be measured.
        NewVersionCheck check = default;
        if (policies is not null)
        {
            source = await WithStoredLengthAsync(source, cancellationToken).ConfigureAwait(false);
            if (source is null) return null;
            check = await policies.CheckNewVersionAsync(destinationContainerName, destinationName, restore: false, source.ContentType, source.ContentLength,
                _telemetry, cancellationToken).ConfigureAwait(false);
        }

        var copy = new NewResourceVersion(new ResourceAddress(destinationContainerName, destinationName), Guid.CreateVersion7().ToString())
        {
            ContentType = source.ContentType,
            Metadata = source.Metadata,
            CopiedFrom = new ResourceOrigin(ContainerName, resourceName, source.VersionId),
            ExpiresAt = ExpiryToStore(copyOptions.ExpiresAt),
            RetainUntil = RetainUntil(check.RetainFor, copyOptions.RetainUntil, copyOptions.LegalHold),
            LegalHold = copyOptions.LegalHold
        };
        WrittenContent? written;
        try
        {
            written = await resourceRepository.CopyResourceAsync(At(resourceName), source.VersionId, copy,
                new ResourceWriteCondition { CreateOnly = createOnly, Conditions = conditions }, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourcefullException exception) when (exception is ResourceConflictException
            or ResourceContainerNotFoundException or ResourcePreconditionFailedException)
        {
            // A copy's only conditions are the destination's, so each of these is about the destination.
            exception.LocateIfUnknown(destinationContainerName, destinationName);
            throw;
        }
        return written is null ? null : await CompleteNewVersionAsync(copy, written).ConfigureAwait(false);
    }

    public Task<ResourceInfo?> MoveResourceAsync(string resourceName, string destinationContainerName, string destinationName,
        ResourceMoveOptions? options = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("move", ContainerName, resourceName,
            () => MoveCoreAsync(resourceName, destinationContainerName, destinationName, options?.SourceConditions, cancellationToken),
            "resourcefull.destination_container", destinationContainerName);

    /// <summary>How many times a move under container policies checks the versions it moves, as uploads replace them.</summary>
    private const int MoveChecksBeforeGivingUp = 5;

    private async Task<ResourceInfo?> MoveCoreAsync(string resourceName, string destinationContainerName, string destinationName,
        ResourceConditions? conditions, CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        ResourceNames.ThrowIfInvalidContainerName(destinationContainerName);
        ResourceNames.ThrowIfInvalidNewResourceName(destinationName);
        if (destinationContainerName == ContainerName && destinationName == resourceName)
            throw new ArgumentException("A resource can't be moved to where it already is.", nameof(destinationName));

        if (policies is null) return await MoveAsync(conditions is { IsEmpty: false } ? conditions : null).ConfigureAwait(false);

        // The versions checked must be the ones that move. A version stored under the name meanwhile becomes its current
        // one, so the move requires the current version it checked, and when another has become current it checks again,
        // a few times at most, so a name rewritten constantly can't keep it going.
        for (int attempt = 1; ; attempt++)
        {
            IReadOnlyList<ResourceInfo> stored = await resourceRepository.GetResourceVersionsAsync(At(resourceName), cancellationToken).ConfigureAwait(false);
            if (stored.Count == 0) return null;
            var versions = new List<ResourceInfo>(stored.Count);
            foreach (ResourceInfo version in stored)
            {
                if (await WithStoredLengthAsync(version, cancellationToken).ConfigureAwait(false) is not { } measured) return null;
                versions.Add(measured);
            }

            await policies.CheckMoveAsync(ContainerName, destinationContainerName, versions, _telemetry, cancellationToken).ConfigureAwait(false);
            ResourceInfo current = versions[0];
            if (conditions?.IfMatchVersionId is { } required && required != current.VersionId)
            {
                throw new ResourcePreconditionFailedException(
                    $"The current version of the resource {resourceName} in the container {ContainerName} isn't " +
                    $"{required}.", current.VersionId, current.ContentHash)
                {
                    ContainerName = ContainerName, ResourceName = resourceName
                };
            }

            try
            {
                return await MoveAsync(new ResourceConditions
                {
                    IfMatchVersionId = current.VersionId,
                    IfMatchContentHash = conditions?.IfMatchContentHash
                }).ConfigureAwait(false);
            }
            catch (ResourcePreconditionFailedException failed) when (failed.CurrentVersionId != current.VersionId
                && attempt < MoveChecksBeforeGivingUp)
            {
                // Another version became current after the check; unmet conditions of the caller's own are reported.
            }
        }

        async Task<ResourceInfo?> MoveAsync(ResourceConditions? sourceConditions)
        {
            bool done;
            try
            {
                done = await resourceRepository.MoveResourceAsync(new ResourceAddress(ContainerName, resourceName),
                    new ResourceAddress(destinationContainerName, destinationName), sourceConditions, cancellationToken).ConfigureAwait(false);
            }
            catch (ResourcefullException exception)
                when (exception is ResourceConflictException or ResourceContainerNotFoundException)
            {
                // A move's conditions are the source's, but a name that exists or a missing container is at the
                // destination.
                exception.LocateIfUnknown(destinationContainerName, destinationName);
                throw;
            }
            if (!done)
            {
                return null;
            }

            ResourceInfo? moved = await resourceRepository.GetResourceInfoAsync(new ResourceAddress(destinationContainerName, destinationName),
                null, cancellationToken).ConfigureAwait(false);
            return moved is null ? null : WithUri(moved);
        }
    }

    /// <summary>
    /// Fills in a version's <see cref="ResourceInfo.ContentLength"/> from its stored content when an early release
    /// recorded none, for the size limits to check: a stream's length is the stored content's, known without reading it.
    /// </summary>
    /// <returns>The version with its length, or <see langword="null"/> if it no longer exists.</returns>
    private async Task<ResourceInfo?> WithStoredLengthAsync(ResourceInfo version, CancellationToken cancellationToken)
    {
        if (version.ContentLength is not null) return version;
        Stream? content = await resourceRepository.GetResourceStreamAsync(At(version.Name), version.VersionId, cancellationToken).ConfigureAwait(false);
        if (content is null) return null;
        await using var contentDisposal = content.ConfigureAwait(false);
        return version with { ContentLength = content.Length };
    }

    public Task<bool> SetExpiryAsync(string resourceName, string? versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_expiry", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            if (!resourceRepository.SupportsExpiry)
                throw new NotSupportedException($"{resourceRepository.GetType().Name} doesn't support expiry.");
            return await resourceRepository.SetExpiryAsync(new ResourceAddress(ContainerName, resourceName), versionId, expiresAt, cancellationToken).ConfigureAwait(false);
        });

    public Task<bool> SetResourceMetadataAsync(string resourceName, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_metadata", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            ArgumentNullException.ThrowIfNull(metadata);
            var copy = new Dictionary<string, string>(metadata);
            options.ThrowIfUnsearchable(copy, nameof(metadata));
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            return await resourceRepository.SetResourceMetadataAsync(At(resourceName), versionId, copy, cancellationToken).ConfigureAwait(false);
        });

    public Task<bool> SetContentTypeAsync(string resourceName, string? versionId, string? contentType,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_content_type", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null) await policies.CheckContentTypeAsync(ContainerName, contentType, _telemetry, cancellationToken).ConfigureAwait(false);
            return await resourceRepository.SetContentTypeAsync(new ResourceAddress(ContainerName, resourceName), versionId,
                contentType, cancellationToken).ConfigureAwait(false);
        });

    public Task<bool> SetRetentionAsync(string resourceName, string? versionId, DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_retention", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            return await resourceRepository.SetRetentionAsync(At(resourceName), versionId, retainUntil, cancellationToken).ConfigureAwait(false);
        });

    public Task<bool> SetLegalHoldAsync(string resourceName, string? versionId, bool hold,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_legal_hold", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            return await resourceRepository.SetLegalHoldAsync(At(resourceName), versionId, hold, cancellationToken).ConfigureAwait(false);
        }, "resourcefull.legal_hold", hold);

    public Task<ResourcePage> GetDeletedPageAsync(ResourceListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("list_deleted", ContainerName, null, async () =>
        {
            options ??= new ResourceListOptions();
            ArgumentOutOfRangeException.ThrowIfLessThan(options.PageSize, 1, nameof(options));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PageSize, ResourceListOptions.MaxPageSize, nameof(options));
            if (options.MetadataMatch is not null)
                throw new ArgumentException("A listing of the bin can't look for metadata.", nameof(options));
            string? prefix = string.IsNullOrEmpty(options.Prefix) ? null : options.Prefix;
            ResourceVersionKey? after = continuationToken is null
                ? null
                : ResourcePageToken.DecodeDeleted(continuationToken, ContainerName, prefix, nameof(continuationToken));

            DeletedVersionPage page = await resourceRepository.GetDeletedPageAsync(ContainerName, prefix, after, options.PageSize,
                cancellationToken).ConfigureAwait(false);
            // Without URIs: a deleted version can't be downloaded.
            return new ResourcePage(page.Versions, page.Next is { } next ? ResourcePageToken.EncodeDeleted(prefix, next) : null);
        }, "resourcefull.page_size", options?.PageSize ?? ResourceListOptions.DefaultPageSize);

    public Task<bool> UndeleteAsync(string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("undelete", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null)
            {
                List<ResourceInfo> returning = [.. (await resourceRepository.GetDeletedVersionsAsync(At(resourceName), cancellationToken).ConfigureAwait(false))
                    .Where(v => versionId is null || v.VersionId == versionId)];
                if (returning.Count == 0) return false;
                await policies.CheckUndeleteAsync(ContainerName, resourceName, returning, _telemetry, cancellationToken).ConfigureAwait(false);
            }
            long undeleted = await resourceRepository.UndeleteAsync(At(resourceName), versionId, cancellationToken).ConfigureAwait(false);
            _telemetry.CountUndeleted(undeleted);
            return undeleted > 0;
        });

    public Task<bool> PurgeDeletedAsync(string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("purge_deleted", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            long purged = await resourceRepository.PurgeDeletedAsync(At(resourceName), versionId, null, cancellationToken).ConfigureAwait(false);
            _telemetry.CountPurged(purged);
            return purged > 0;
        });

    public Task<ContainerInfo?> GetInfoAsync(CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("get_container", ContainerName, null, async () =>
            await resourceRepository.GetContainerInfoAsync(ContainerName, cancellationToken).ConfigureAwait(false) is { } stored
                ? ResourceServiceClient.WithUri(stored, uriProvider)
                : null);

    public Task<bool> SetMetadataAsync(IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("set_container_metadata", ContainerName, null, async () =>
        {
            ArgumentNullException.ThrowIfNull(metadata);
            if (policies is not null) await policies.ThrowIfReadOnlyAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false);
            return await resourceRepository.SetContainerMetadataAsync(ContainerName,
                new Dictionary<string, string>(metadata), cancellationToken).ConfigureAwait(false);
        });

    public Task<long> PruneAsync(CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("prune_container", ContainerName, null, activity => PruneAllAsync(activity, cancellationToken));

    public Task<long> IndexMetadataAsync(CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("index_metadata", ContainerName, null, async activity =>
        {
            long indexed = await resourceRepository.IndexMetadataAsync(ContainerName, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("resourcefull.indexed", indexed);
            _telemetry.CountIndexed(indexed);
            return indexed;
        });

    public Task<ContainerUsage> GetUsageAsync(CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("get_usage", ContainerName, null, () => resourceRepository.GetContainerUsageAsync(ContainerName, cancellationToken));

    public Task<ContainerPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("get_container_policy", ContainerName, null, async () =>
        {
            // UseVersioning's limit fills in when neither the container's policy nor the defaults set MaxVersions, so the
            // result always has the limit in force.
            var storeWide = new ContainerPolicy { MaxVersions = options.MaxVersions };
            return policies is null
                ? storeWide
                : ContainerPolicies.Merge(await policies.GetAsync(ContainerName, _telemetry, cancellationToken).ConfigureAwait(false), storeWide);
        });

    /// <summary>The most versions that <see cref="GetTopVersionsAsync"/> and the service client's <c>GetExpiredVersionsAsync</c> return.</summary>
    internal const int MaxListedVersions = 1000;

    public Task<IReadOnlyList<ResourceInfo>> GetTopVersionsAsync(ResourceVersionOrder order, int count = 20,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync<IReadOnlyList<ResourceInfo>>("list_top", ContainerName, null, async () =>
        {
            if (!Enum.IsDefined(order)) throw new ArgumentOutOfRangeException(nameof(order), order, null);
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxListedVersions);
            return WithUris(await resourceRepository.GetTopVersionsAsync(ContainerName, order, count, cancellationToken).ConfigureAwait(false));
        }, "resourcefull.order", order switch
        {
            ResourceVersionOrder.LargestFirst => "largest_first",
            ResourceVersionOrder.ExpiringFirst => "expiring_first",
            _ => null
        });

    public Task<bool> ExistsAsync(string resourceName, string? versionId = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("exists", ContainerName, resourceName, async () =>
        {
            ResourceNames.ThrowIfInvalidResourceName(resourceName);
            return await resourceRepository.GetResourceInfoAsync(At(resourceName), versionId, cancellationToken).ConfigureAwait(false) != null;
        });

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pruning older versions of {ContainerName}/{ResourceName} failed; they stay until a later write of the name prunes them, or they expire.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception, string containerName, string resourceName);
}
