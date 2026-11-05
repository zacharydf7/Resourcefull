using System.Diagnostics;
using Resourcefull.Providers;

namespace Resourcefull;

// Upload sessions: a version's content arrives over several appends, then the session is completed, which measures
// and hashes the stored content, inspects it, and makes the version current as an upload would.
internal partial class ResourceContainerClient
{
    public Task<ResourceUpload> StartUploadAsync(string resourceName, ResourceUploadSessionOptions? options = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("upload_start", ContainerName, resourceName, () => StartUploadCoreAsync(resourceName, options,
            cancellationToken));

    private async Task<ResourceUpload> StartUploadCoreAsync(string resourceName, ResourceUploadSessionOptions? options,
        CancellationToken cancellationToken)
    {
        ResourceNames.ThrowIfInvalidNewResourceName(resourceName);
        options ??= new ResourceUploadSessionOptions();
        StoreOptions.ThrowIfUnsearchable(options.Metadata.AsReadOnly(), nameof(options));
        NewVersionCheck check = policies is null ? default : await policies.CheckNewVersionAsync(ContainerName, resourceName,
            restore: false, options.Headers?.ContentType, options.Headers?.ContentLength, _telemetry, cancellationToken).ConfigureAwait(false);

        var version = new NewResourceVersion(At(resourceName), Guid.CreateVersion7().ToString())
        {
            ContentType = options.Headers?.ContentType,
            DeclaredLength = options.Headers?.ContentLength,
            Metadata = new Dictionary<string, string>(options.Metadata),
            ExpiresAt = options.ExpiresAt,
            RetainUntil = RetainUntil(check.RetainFor, options.RetainUntil, options.LegalHold),
            LegalHold = options.LegalHold
        };
        await resourceRepository.StartStagedVersionAsync(version, cancellationToken).ConfigureAwait(false);
        return new ResourceUpload(ContainerName, resourceName, version.VersionId, 0, version.DeclaredLength);
    }

    public Task<ResourceUpload?> GetUploadAsync(string resourceName, string uploadId, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("upload_get", ContainerName, resourceName, async () =>
        {
            ThrowIfInvalidUpload(resourceName, uploadId);
            StagedVersion? staged = await resourceRepository.GetStagedVersionAsync(At(resourceName), uploadId, cancellationToken)
                .ConfigureAwait(false);
            return staged is null ? null : Describe(staged);
        });

    public Task<ResourceUpload?> AppendUploadAsync(string resourceName, string uploadId, long offset, Stream content,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("upload_append", ContainerName, resourceName, async activity =>
        {
            ThrowIfInvalidUpload(resourceName, uploadId);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentNullException.ThrowIfNull(content);
            activity?.SetTag("resourcefull.offset", offset);

            if (await resourceRepository.GetStagedVersionAsync(At(resourceName), uploadId, cancellationToken).ConfigureAwait(false)
                is not { } staged)
            {
                return null;
            }
            if (policies is not null
                && await policies.CheckAppendAsync(ContainerName, offset, _telemetry, cancellationToken).ConfigureAwait(false) is { } limit)
            {
                content = new LimitedContentStream(content, limit);
            }

            long? stored;
            try
            {
                stored = await resourceRepository.AppendStagedContentAsync(At(resourceName), uploadId, offset, content,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not ResourceUploadOffsetException)
            {
                // An append that fails partway may have stored part of its content, which counts as appended.
                await RecordAppendedSinceAsync(resourceName, uploadId, offset).ConfigureAwait(false);
                throw;
            }
            if (stored is not { } length) return null;
            _telemetry.RecordAppended(length - offset);
            return Describe(staged with { StoredLength = length });
        });

    /// <summary>
    /// Counts what a failed append stored, as far as the session can say within a few seconds, so a database that's down
    /// doesn't hold up the append's own failure; a failure to find out is ignored.
    /// </summary>
    private async Task RecordAppendedSinceAsync(string resourceName, string uploadId, long offset)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (await resourceRepository.GetStagedVersionAsync(At(resourceName), uploadId, timeout.Token).ConfigureAwait(false)
                is { StoredLength: var length } && length > offset)
            {
                _telemetry.RecordAppended(length - offset);
            }
        }
        catch
        {
            // The append's own failure is the one to report.
        }
    }

    private ResourceUpload Describe(StagedVersion staged) =>
        new(ContainerName, staged.Version.Resource.ResourceName, staged.Version.VersionId, staged.StoredLength,
            staged.Version.DeclaredLength);

    public Task<ResourceInfo?> CompleteUploadAsync(string resourceName, string uploadId,
        ResourceWriteMode mode = ResourceWriteMode.Create, ResourceConditions? conditions = null,
        CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("upload_complete", ContainerName, resourceName, activity =>
        {
            activity?.SetTag("resourcefull.write_mode", WriteModeTag(mode));
            return CompleteUploadCoreAsync(resourceName, uploadId, mode, conditions, cancellationToken);
        }, "resourcefull.conditional", conditions is { IsEmpty: false });

    private async Task<ResourceInfo?> CompleteUploadCoreAsync(string resourceName, string uploadId, ResourceWriteMode mode,
        ResourceConditions? conditions, CancellationToken cancellationToken)
    {
        ThrowIfInvalidUpload(resourceName, uploadId);
        conditions = conditions is { IsEmpty: false } ? conditions : null;
        bool createOnly = IsCreate(mode, conditions, "conditions", nameof(mode));
        ResourceAddress location = At(resourceName);

        if (await resourceRepository.GetStagedVersionAsync(location, uploadId, cancellationToken).ConfigureAwait(false) is not { } staged)
            return null;
        NewResourceVersion version = staged.Version;

        // Checked before the content is read, since that read is the costly part; the provider decides the write
        // condition again as the version becomes current, and refuses a length that changed meanwhile.
        if (version.DeclaredLength is { } declared && declared != staged.StoredLength)
        {
            throw new ResourceContentLengthMismatchException($"The upload {uploadId} of the resource {resourceName} in the " +
                $"container {ContainerName} has {staged.StoredLength} bytes stored, not the {declared} it declared.")
            {
                ContainerName = ContainerName, ResourceName = resourceName
            };
        }
        if (policies is not null)
        {
            await policies.CheckNewVersionAsync(ContainerName, resourceName, restore: false, version.ContentType,
                staged.StoredLength, _telemetry, cancellationToken).ConfigureAwait(false);
        }
        if (createOnly || conditions is not null)
        {
            ResourceInfo? current = await resourceRepository.GetResourceInfoAsync(location, null, cancellationToken).ConfigureAwait(false);
            if (createOnly && current is not null) throw NameExists(resourceName);
            conditions?.ThrowIfNotMet(location, current?.VersionId, current?.ContentHash);
        }

        // Measured, hashed and inspected from what was stored, since appends can come from different processes: a hash
        // can't carry its state from one to the next.
        Stream? content = await resourceRepository.GetStagedStreamAsync(location, uploadId, cancellationToken).ConfigureAwait(false);
        if (content is null) return null;
        WrittenContent measured;
        await using (content.ConfigureAwait(false))
        {
            var hashing = new HashingStream(content);
            InspectedContentStream? inspected = uploadInspector is null
                ? null
                : InspectedContentStream.Start(uploadInspector, version, staged.StoredLength, hashing, _telemetry, "upload_complete",
                    cancellationToken);
            try
            {
                await (inspected ?? (Stream)hashing).CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            }
            catch (ResourceContentRefusedException)
            {
                // Refused content is never to be stored, so the session goes too.
                await resourceRepository.DeleteStagedVersionAsync(location, uploadId, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (inspected is not null) await inspected.DisposeAsync().ConfigureAwait(false);
            }
            measured = hashing.Complete(null, DateTimeOffset.UnixEpoch);
        }
        // An append that landed after the checks above would otherwise be completed unchecked; the provider then ties
        // what it makes current to this length.
        if (measured.ContentLength != staged.StoredLength)
        {
            throw new ResourceUploadOffsetException($"The upload {uploadId} of the resource {resourceName} in the container " +
                $"{ContainerName} grew from {staged.StoredLength} to {measured.ContentLength} bytes while it was being completed.",
                measured.ContentLength)
            {
                ContainerName = ContainerName, ResourceName = resourceName
            };
        }

        WrittenContent? written;
        try
        {
            written = await resourceRepository.CompleteStagedVersionAsync(location, uploadId, measured.ContentLength,
                measured.ContentHash, new ResourceWriteCondition { CreateOnly = createOnly, Conditions = conditions },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ResourceConflictException exception)
        {
            throw NameExists(resourceName, exception);
        }
        if (written is null) return null;

        _telemetry.RecordUploaded("upload_complete", written.ContentLength);
        return await CompleteNewVersionAsync(version, written).ConfigureAwait(false);
    }

    public Task<bool> CancelUploadAsync(string resourceName, string uploadId, CancellationToken cancellationToken = default)
        => _telemetry.RunAsync("upload_cancel", ContainerName, resourceName, () =>
        {
            ThrowIfInvalidUpload(resourceName, uploadId);
            return resourceRepository.DeleteStagedVersionAsync(At(resourceName), uploadId, cancellationToken);
        });

    private ResourceConflictException NameExists(string resourceName, Exception? innerException = null)
    {
        string message = $"The resource {resourceName} already exists in the container {ContainerName}. Complete the " +
            $"upload with {nameof(ResourceWriteMode)}.{nameof(ResourceWriteMode.Replace)} to replace it.";
        return innerException is null
            ? new(message) { ContainerName = ContainerName, ResourceName = resourceName }
            : new(message, innerException) { ContainerName = ContainerName, ResourceName = resourceName };
    }

    private static void ThrowIfInvalidUpload(string resourceName, string uploadId)
    {
        ResourceNames.ThrowIfInvalidResourceName(resourceName);
        ArgumentException.ThrowIfNullOrEmpty(uploadId);
    }
}
