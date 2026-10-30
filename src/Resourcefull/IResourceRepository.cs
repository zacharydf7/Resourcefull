using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull;

/// <summary>
/// The storage behind Resourcefull: implement this to add a provider, and register it with
/// <see cref="ResourcefullOptionsBuilder.UseProvider{TRepository}(Microsoft.Extensions.DependencyInjection.ServiceLifetime)"/>.
/// This interface follows semantic versioning: it changes incompatibly only in a major release.
/// </summary>
/// <remarks>
/// <para>
/// The clients only guarantee that container and resource names are not blank, are within the
/// <see cref="ResourceNames"/> limits, and don't start or end with whitespace; any other characters, including
/// <c>/</c>, <c>\</c> and <c>..</c>, can reach a provider. Version IDs passed to reads and deletes are not checked
/// at all. A provider must encode or escape names and version IDs before using them as file paths or storage keys.
/// </para>
/// <para>Every implementation must:</para>
/// <list type="bullet">
/// <item>Compare container names, resource names and version IDs ordinally (case-sensitive, culture-insensitive).</item>
/// <item>Treat a version as current when it was stored most recently, never by <see cref="ResourceInfo.LastModified"/>,
/// and never leave a name with more than one current version, however writes and deletes interleave.</item>
/// <item>Store the <see cref="WrittenContent.ContentLength"/>, <see cref="WrittenContent.ContentHash"/> and
/// <see cref="WrittenContent.LastModified"/> that <see cref="AddResourceAsync(string, ResourceInfo, Stream, bool, CancellationToken)"/> returns with the version, and return
/// them on every read.</item>
/// <item>Observe every <c>cancellationToken</c>: a cancelled operation throws <see cref="OperationCanceledException"/>
/// and never reports success or a partial result.</item>
/// <item>Be safe to call concurrently for the lifetime it is registered with.</item>
/// </list>
/// </remarks>
public interface IResourceRepository
{
    // Resources

    /// <summary>
    /// Adds a resource as the new current version of its name. Implementations must not silently discard the write
    /// when the container is absent, and must decide that from the write itself rather than a prior check. The same
    /// applies to <paramref name="createOnly"/>, and concurrent writers must never leave more than one current version.
    /// </summary>
    /// <param name="resourceContainerName">The container to add to.</param>
    /// <param name="resourceInfo">The version to store; <see cref="ResourceInfo.ContentLength"/>, when set, is the length the caller declared.</param>
    /// <param name="stream">
    /// The content, read from its current position to its end. A provider that retries may seek back to that position
    /// when the stream can seek, and must not otherwise move it.
    /// </param>
    /// <param name="createOnly">Store only if no version of the name exists.</param>
    /// <param name="cancellationToken">Cancels the write; a cancelled write stores nothing.</param>
    /// <returns>
    /// The length and hash of what was stored, measured by reading <paramref name="stream"/> through a
    /// <see cref="HashingStream"/>; the measured length, not <see cref="ResourceInfo.ContentLength"/>, is stored. Also
    /// the time the version became current, taken when the write makes it current (not before its content was read)
    /// and stored as its <see cref="ResourceInfo.LastModified"/>; the caller's <see cref="ResourceInfo.LastModified"/>
    /// is ignored.
    /// </returns>
    /// <exception cref="ResourceContentLengthMismatchException">
    /// <paramref name="resourceInfo"/> declares a content length that differs from the stream; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceContainerNotFoundException">The named container does not exist.</exception>
    /// <exception cref="ResourceConflictException">
    /// <paramref name="createOnly"/> is set and the name already exists, or the name already has a version with this
    /// <see cref="ResourceInfo.VersionId"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; nothing was stored.</exception>
    Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a resource as <see cref="AddResourceAsync(string, ResourceInfo, Stream, bool, CancellationToken)"/> does,
    /// storing it only if the name meets <paramref name="condition"/>. The clients call this only for an upload with
    /// <see cref="ResourceUploadOptions.Conditions"/>. A provider that supports conditions must decide them from the
    /// write itself, at the point the version would become current, so a concurrent change to the name can't come
    /// between the check and the write. The default implementation handles an upload without conditions and throws
    /// <see cref="NotSupportedException"/> for one with them.
    /// </summary>
    /// <param name="resourceContainerName">The container to add to.</param>
    /// <param name="resourceInfo">The version to store.</param>
    /// <param name="stream">The content, as for the other overload.</param>
    /// <param name="condition">What the name must be like for the version to be stored.</param>
    /// <param name="cancellationToken">Cancels the write; a cancelled write stores nothing.</param>
    /// <returns>As for the other overload.</returns>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <see cref="ResourceWriteCondition.Conditions"/>; nothing was stored.
    /// </exception>
    /// <exception cref="NotSupportedException">The provider doesn't support conditional writes.</exception>
    Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream,
        ResourceWriteCondition condition, CancellationToken cancellationToken = default)
        => condition.Conditions is null or { IsEmpty: true }
            ? AddResourceAsync(resourceContainerName, resourceInfo, stream, condition.CreateOnly, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} doesn't support conditional uploads.");

    /// <summary>
    /// Lists the current version of every resource in a container, each with <see cref="ResourceInfo.IsLatest"/> set.
    /// </summary>
    /// <returns>The resources; empty when the container is empty or does not exist.</returns>
    Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists up to <paramref name="pageSize"/> current versions in a container, ordered by name in ordinal order
    /// (<see cref="StringComparer.Ordinal"/>), each with <see cref="ResourceInfo.IsLatest"/> set. A provider may use
    /// its storage's binary order instead, as long as its ordering and its <paramref name="afterName"/> comparison agree:
    /// SQL Server's binary collations, for one, sort a name after itself followed by characters below U+0020, since
    /// they pad the shorter name with spaces. A provider should answer this without reading the whole container. The
    /// default implementation, kept so providers written for 3.0 still compile and work, filters and sorts
    /// <see cref="GetAllResourceInfoAsync"/> in memory.
    /// </summary>
    /// <param name="resourceContainerName">The container to list.</param>
    /// <param name="prefix">Only names that start with this, ordinally; <see langword="null"/> for every name.</param>
    /// <param name="afterName">Only names after this one, in the listing's order; <see langword="null"/> to start from the first.</param>
    /// <param name="pageSize">The most versions to return.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The versions; empty when there are no more, or the container does not exist.</returns>
    async Task<List<ResourceInfo>> GetResourcePageAsync(string resourceContainerName, string? prefix, string? afterName,
        int pageSize, CancellationToken cancellationToken = default) =>
        [.. (await GetAllResourceInfoAsync(resourceContainerName, cancellationToken))
            .Where(r => (prefix is null || r.Name.StartsWith(prefix, StringComparison.Ordinal))
                && (afterName is null || string.CompareOrdinal(r.Name, afterName) > 0))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Take(pageSize)];

    /// <summary>
    /// Lists every stored version of a resource: the current version first, then the rest from the most recently
    /// stored to the oldest.
    /// </summary>
    /// <returns>The versions; empty when the resource or container does not exist.</returns>
    Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets one version of a resource, or the current version when <paramref name="versionId"/> is null, with
    /// <see cref="ResourceInfo.IsLatest"/> set accurately.
    /// </summary>
    /// <returns>The version, or <see langword="null"/> when it does not exist.</returns>
    Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the content of one version of a resource, or of the current version when <paramref name="versionId"/>
    /// is null, as a read-only, forward-only stream: <see cref="Stream.Length"/> is the content's full length,
    /// <see cref="Stream.Position"/> the bytes read so far, and seeking, setting the position, writing and
    /// <see cref="Stream.SetLength"/> throw <see cref="NotSupportedException"/>. The caller owns and disposes it.
    /// </summary>
    /// <returns>The content, or <see langword="null"/> when the version does not exist.</returns>
    Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the content of a version as the other overload does, but starting at <paramref name="offset"/>, for a
    /// download of a range: <see cref="Stream.Length"/> is still the content's full length, and
    /// <see cref="Stream.Position"/> starts at <paramref name="offset"/>, or at the content's length when
    /// <paramref name="offset"/> is past it. Reading may go on past the range, so the client reads only
    /// <paramref name="length"/> bytes; a provider can stop reading storage there, as the SQL Server provider does.
    /// The default implementation opens the whole content and reads past the bytes before
    /// <paramref name="offset"/>, which costs as much as reading them: override it if the store can start at an offset.
    /// </summary>
    /// <param name="resourceContainerName">The container of the resource.</param>
    /// <param name="resourceName">The resource's name.</param>
    /// <param name="versionId">The version to open, or <see langword="null"/> for the current version.</param>
    /// <param name="offset">Where the range starts; not negative.</param>
    /// <param name="length">How many bytes the client will read, not negative; to the end when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels opening the content.</param>
    /// <returns>The content, or <see langword="null"/> when the version does not exist.</returns>
    async Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId,
        long offset, long? length, CancellationToken cancellationToken = default)
    {
        Stream? content = await GetResourceStreamAsync(resourceContainerName, resourceName, versionId, cancellationToken);
        if (content is null) return null;
        try
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(offset, 1, 81920));
            try
            {
                for (long skipped = 0, read = 1; skipped < offset && read > 0; skipped += read)
                    read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset - skipped)), cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return content;
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Deletes one version of a resource, or with no <paramref name="versionId"/> every version of it. Deleting the
    /// current version alone makes the most recently stored remaining version current.
    /// </summary>
    /// <returns><see langword="true"/> if anything was deleted; <see langword="false"/> if there was nothing to delete,
    /// including when a concurrent delete removed it first.</returns>
    Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes as <see cref="DeleteResourceAsync(string, string, string?, CancellationToken)"/> does, only if the name's
    /// current version meets <paramref name="conditions"/>, decided by the delete itself. A name with no current
    /// version meets no condition. The clients call this only for a delete with conditions. The default implementation
    /// handles empty conditions and throws <see cref="NotSupportedException"/> otherwise.
    /// </summary>
    /// <param name="resourceContainerName">The container of the resource.</param>
    /// <param name="resourceName">The resource's name.</param>
    /// <param name="versionId">The version to delete, or <see langword="null"/> for every version.</param>
    /// <param name="conditions">What the name's current version must be like.</param>
    /// <param name="cancellationToken">Cancels the delete; a cancelled delete deletes nothing.</param>
    /// <returns>As for the other overload.</returns>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <paramref name="conditions"/>, or the name doesn't exist; nothing was deleted.
    /// </exception>
    /// <exception cref="NotSupportedException">The provider doesn't support conditional deletes.</exception>
    Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId,
        ResourceConditions conditions, CancellationToken cancellationToken = default)
        => conditions.IsEmpty
            ? DeleteResourceAsync(resourceContainerName, resourceName, versionId, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} doesn't support conditional deletes.");

    // Resource Containers

    /// <summary>
    /// Adds a container, storing a copy of its <see cref="ResourceContainerInfo.Metadata"/>. Like
    /// <see cref="AddResourceAsync(string, ResourceInfo, Stream, bool, CancellationToken)"/>, a duplicate is detected by the write itself. Storing
    /// <see cref="ResourceContainerInfo.Uri"/> is optional: the clients replace it with the URI provider's whenever
    /// they return a container or version.
    /// </summary>
    /// <exception cref="ResourceConflictException">A container with the same name already exists.</exception>
    Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every container, each with its own copy of the stored metadata.
    /// </summary>
    Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a container, with its own copy of the stored metadata.
    /// </summary>
    /// <returns>The container, or <see langword="null"/> when it does not exist.</returns>
    Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a container and every resource and version in it. A provider may do this in several steps for a large
    /// container: if it is cancelled or fails partway, every version it hasn't deleted must still be whole, each
    /// remaining name must still have exactly one current version, and calling it again must finish the job.
    /// </summary>
    /// <returns><see langword="true"/> if the container existed and was deleted; otherwise <see langword="false"/>.</returns>
    Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a copy of one version of a resource, or of the current version when <paramref name="sourceVersionId"/> is
    /// null, as a new version described by <paramref name="destination"/>, with the source's content and hash. It's
    /// stored as <see cref="AddResourceAsync(string, ResourceInfo, Stream, ResourceWriteCondition, CancellationToken)"/>
    /// stores an upload, deciding conflicts and conditions at the destination the same way, and keeps
    /// <see cref="ResourceInfo.CopiedFrom"/>. The default implementation reads the content and stores it through that
    /// method; a provider that can copy content within its store should override it.
    /// </summary>
    /// <param name="source">The resource to copy.</param>
    /// <param name="sourceVersionId">The version to copy, or <see langword="null"/> for the current version.</param>
    /// <param name="destination">The new version: its container, name, version ID, type, metadata and origin.</param>
    /// <param name="condition">How to treat the destination name's current version, as for an upload.</param>
    /// <param name="cancellationToken">Cancels the copy; a canceled copy stores nothing.</param>
    /// <returns>What was stored, or <see langword="null"/> when the source version doesn't exist.</returns>
    async Task<WrittenContent?> CopyResourceAsync(ResourceLocation source, string? sourceVersionId, ResourceInfo destination,
        ResourceWriteCondition condition, CancellationToken cancellationToken = default)
    {
        await using Stream? content = await GetResourceStreamAsync(source.ContainerName, source.ResourceName, sourceVersionId, cancellationToken);
        return content is null ? null : await AddResourceAsync(destination.ContainerName, destination, content, condition, cancellationToken);
    }

    /// <summary>
    /// Moves a resource, every version of it, to <paramref name="destination"/>, keeping each version's ID,
    /// <see cref="ResourceInfo.LastModified"/>, content and attributes, all at once: the versions are never in both
    /// places, or in neither, or split between them. The destination name must have no versions, and its container must
    /// exist. The default implementation throws <see cref="NotSupportedException"/>, because a move made of copies and
    /// deletes could stop halfway.
    /// </summary>
    /// <param name="source">The resource to move.</param>
    /// <param name="destination">Where to move it.</param>
    /// <param name="sourceConditions">What the source's current version must be like, decided by the move itself.</param>
    /// <param name="cancellationToken">Cancels the move; a canceled move changes nothing.</param>
    /// <returns><see langword="true"/> if it moved; <see langword="false"/> if the source has no versions.</returns>
    /// <exception cref="ResourceConflictException">The destination name has versions.</exception>
    /// <exception cref="ResourceContainerNotFoundException">The destination container doesn't exist.</exception>
    /// <exception cref="ResourcePreconditionFailedException">The source's current version doesn't meet <paramref name="sourceConditions"/>.</exception>
    /// <exception cref="NotSupportedException">The provider doesn't move resources.</exception>
    Task<bool> MoveResourceAsync(ResourceLocation source, ResourceLocation destination, ResourceConditions? sourceConditions,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't move resources.");

    /// <summary>
    /// Stores a version as it was in another store, for an import: with <paramref name="version"/>'s
    /// <see cref="ResourceInfo.VersionId"/> and <see cref="ResourceInfo.LastModified"/> rather than new ones, and its
    /// type, metadata, <see cref="ResourceInfo.CopiedFrom"/> and <see cref="ResourceInfo.ExpiresAt"/>. Otherwise it's
    /// stored as <see cref="AddResourceAsync(string, ResourceInfo, Stream, ResourceWriteCondition, CancellationToken)"/>
    /// stores an upload: measured and hashed, refused if <see cref="ResourceInfo.ContentLength"/> doesn't match, and made
    /// the name's current version. The default implementation throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="resourceContainerName">The container to store it in.</param>
    /// <param name="version">The version as it was.</param>
    /// <param name="content">Its content.</param>
    /// <param name="cancellationToken">Cancels the write; a canceled write stores nothing.</param>
    /// <returns>What was stored.</returns>
    /// <exception cref="ResourceConflictException">The name already has a version with that ID.</exception>
    /// <exception cref="NotSupportedException">The provider doesn't import versions.</exception>
    Task<WrittenContent> ImportVersionAsync(string resourceContainerName, ResourceInfo version, Stream content,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't import versions.");

    /// <summary>
    /// Lists the versions, in every container, whose <see cref="ResourceInfo.ExpiresAt"/> is at or before
    /// <paramref name="now"/>, ordered by <see cref="ResourceInfo.ExpiresAt"/> and then by version ID (ordinally), each
    /// with <see cref="ResourceInfo.IsLatest"/> set. Expiry calls it every interval, so a provider should answer it from
    /// an index of the versions that expire. The default implementation throws <see cref="NotSupportedException"/>:
    /// listing every container to find them would cost too much.
    /// </summary>
    /// <param name="now">The time to compare expiries with.</param>
    /// <param name="maxVersions">The most versions to return.</param>
    /// <param name="after">
    /// The last version of the previous batch, to list the ones after it in that order; <see langword="null"/> to start
    /// from the first. A sweep goes through every expired version once this way, whatever it skips or fails to delete.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <exception cref="NotSupportedException">The provider doesn't support expiry.</exception>
    Task<List<ResourceInfo>> GetExpiredAsync(DateTimeOffset now, int maxVersions, ResourceInfo? after = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Deletes one version, as <see cref="DeleteResourceAsync(string, string, string?, CancellationToken)"/> does, only
    /// if its <see cref="ResourceInfo.ExpiresAt"/> is still at or before <paramref name="now"/>, decided by the delete
    /// itself: <see cref="SetExpiryAsync"/> may have changed it since it was listed.
    /// </summary>
    /// <returns><see langword="true"/> if it was deleted; <see langword="false"/> if it's gone or no longer expired.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support expiry, as by default.</exception>
    Task<bool> DeleteExpiredVersionAsync(ResourceLocation resource, string versionId, DateTimeOffset now, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Sets one version's <see cref="ResourceInfo.ExpiresAt"/>, or clears it with <see langword="null"/>, changing nothing
    /// else about it. <see cref="AddResourceAsync(string, ResourceInfo, Stream, ResourceWriteCondition, CancellationToken)"/>
    /// and <see cref="CopyResourceAsync"/> store it from the version they're given, and every read returns it.
    /// </summary>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support expiry, as by default.</exception>
    Task<bool> SetExpiryAsync(ResourceLocation resource, string versionId, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Gets what a container holds: the total <see cref="ResourceInfo.ContentLength"/> of every version, and how many
    /// resources and versions there are. Container policies read it before each write they limit, so a provider should
    /// answer it without reading content. The default implementation lists every resource's versions, one name at a
    /// time; override it if the store can add them up itself.
    /// </summary>
    /// <param name="resourceContainerName">The container.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The container's usage; nothing stored when the container doesn't exist.</returns>
    async Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        List<ResourceInfo> resources = await GetAllResourceInfoAsync(resourceContainerName, cancellationToken);
        long storedBytes = 0, versionCount = 0;
        foreach (ResourceInfo resource in resources)
        {
            foreach (ResourceInfo version in await GetResourceVersionsAsync(resourceContainerName, resource.Name, cancellationToken))
            {
                storedBytes += version.ContentLength ?? 0;
                versionCount++;
            }
        }

        return new ResourceContainerUsage(storedBytes, resources.Count, versionCount);
    }
}
