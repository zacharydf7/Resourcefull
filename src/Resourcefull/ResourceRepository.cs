using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull.Providers;

/// <summary>
/// The storage behind Resourcefull: derive from this to add a provider, and register it with
/// <see cref="ResourcefullOptionsBuilder.UseProvider{TRepository}(Microsoft.Extensions.DependencyInjection.ServiceLifetime)"/>.
/// The abstract members are what every provider must do; the virtual ones have defaults that work, more slowly than a
/// provider could, or throw <see cref="NotSupportedException"/> so the feature that needs them reports that it isn't
/// supported. This class follows semantic versioning: it changes incompatibly only in a major release.
/// <para>Coming in 4.0.</para>
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
/// <see cref="WrittenContent.LastModified"/> that <see cref="AddResourceAsync"/> returns with the version, and return
/// them on every read.</item>
/// <item>Observe every <c>cancellationToken</c>: a canceled operation throws <see cref="OperationCanceledException"/>
/// and never reports success or a partial result.</item>
/// <item>Be safe to call concurrently for the lifetime it is registered with.</item>
/// </list>
/// </remarks>
public abstract class ResourceRepository
{
    // Resources

    /// <summary>
    /// Adds a resource as the new current version of its name, if the name meets <paramref name="condition"/>.
    /// Implementations must not silently discard the write when the container is absent. They must decide that, and
    /// <paramref name="condition"/>, in the write itself, at the point the version would become current, not in a
    /// check beforehand, so a concurrent change to the name can't come between the check and the write. Concurrent
    /// writers must never leave more than one current version.
    /// </summary>
    /// <param name="version">The version to store; <see cref="NewResourceVersion.DeclaredLength"/>, when set, is the length the caller declared.</param>
    /// <param name="content">
    /// The content, read from its current position to its end. A provider that retries may seek back to that position
    /// when the stream can seek, and must not otherwise move it. The provider must read until a read returns nothing
    /// before it makes the version current, and store nothing if that read throws: that read waits for an
    /// <see cref="IResourceUploadInspector"/>'s answer, and throws if it refuses the content.
    /// </param>
    /// <param name="condition">What the name must be like for the version to be stored; <see langword="null"/> for no condition.</param>
    /// <param name="cancellationToken">Cancels the write; a canceled write stores nothing.</param>
    /// <returns>
    /// The length and hash of what was stored, measured by reading <paramref name="content"/> through a
    /// <see cref="HashingStream"/>; the measured length, not <see cref="NewResourceVersion.DeclaredLength"/>, is stored.
    /// Also the time the version became current, taken when the write makes it current (not before its content was
    /// read) and stored as its <see cref="ResourceInfo.LastModified"/>.
    /// </returns>
    /// <exception cref="ResourceContentLengthMismatchException">
    /// <paramref name="version"/> declares a content length that differs from the stream; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceContainerNotFoundException">The named container does not exist.</exception>
    /// <exception cref="ResourceConflictException">
    /// <see cref="ResourceWriteCondition.CreateOnly"/> is set and the name already exists, or the name already has a
    /// version with this <see cref="NewResourceVersion.VersionId"/>.
    /// </exception>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <see cref="ResourceWriteCondition.Conditions"/>; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceUploadLimitException">
    /// The content is larger than the provider can store in one object; nothing was stored.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; nothing was stored.</exception>
    public abstract Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
        ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the current version of every resource in a container, each with <see cref="ResourceInfo.IsLatest"/> set.
    /// </summary>
    /// <param name="resourceContainerName">The container to list.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The resources; empty when the container is empty or does not exist.</returns>
    public abstract Task<IReadOnlyList<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists up to <paramref name="pageSize"/> current versions in a container, ordered by name in ordinal order
    /// (<see cref="StringComparer.Ordinal"/>), each with <see cref="ResourceInfo.IsLatest"/> set. A provider may use
    /// its storage's binary order instead, as long as its ordering and its <paramref name="afterName"/> comparison agree:
    /// SQL Server's binary collations, for one, sort a name after itself followed by characters below U+0020, since
    /// they pad the shorter name with spaces. A provider should answer this without reading the whole container. The
    /// default implementation filters and sorts <see cref="GetAllResourceInfoAsync"/> in memory.
    /// </summary>
    /// <param name="resourceContainerName">The container to list.</param>
    /// <param name="prefix">Only names that start with this, ordinally; <see langword="null"/> for every name.</param>
    /// <param name="afterName">Only names after this one, in the listing's order; <see langword="null"/> to start from the first.</param>
    /// <param name="pageSize">The most versions to return.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The versions; empty when there are no more, or the container does not exist.</returns>
    public virtual async Task<IReadOnlyList<ResourceInfo>> GetResourcePageAsync(string resourceContainerName, string? prefix,
        string? afterName, int pageSize, CancellationToken cancellationToken = default) =>
        [.. (await GetAllResourceInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false))
            .Where(r => (prefix is null || r.Name.StartsWith(prefix, StringComparison.Ordinal))
                && (afterName is null || string.CompareOrdinal(r.Name, afterName) > 0))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .Take(pageSize)];

    /// <summary>
    /// Lists every stored version of a resource: the current version first, then the rest from the most recently
    /// stored to the oldest.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The versions; empty when the resource or container does not exist.</returns>
    public abstract Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceLocation resource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every stored version of up to <paramref name="maxNames"/> names in a container: the names after
    /// <paramref name="afterName"/> in <see cref="GetResourcePageAsync"/>'s order, and each name's versions in
    /// <see cref="GetResourceVersionsAsync"/>'s, the current version first. An export reads a container through it a
    /// page at a time, until a page comes back empty, so return an empty page only when no names follow
    /// <paramref name="afterName"/>: a name deleted while the page is read is left out rather than ending the listing.
    /// A provider should answer it in one query; the default implementation lists the names with
    /// <see cref="GetResourcePageAsync"/> and then each name's versions.
    /// </summary>
    /// <param name="resourceContainerName">The container to list.</param>
    /// <param name="afterName">
    /// Only names after this one, in the listing's order; <see langword="null"/> to start from the first.
    /// </param>
    /// <param name="maxNames">The most names to list the versions of.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>
    /// The versions, grouped by name; empty when there are no more names, or the container does not exist.
    /// </returns>
    public virtual async Task<IReadOnlyList<ResourceInfo>> GetVersionPageAsync(string resourceContainerName, string? afterName,
        int maxNames, CancellationToken cancellationToken = default)
    {
        var versions = new List<ResourceInfo>();
        int names = 0;
        while (names < maxNames)
        {
            int wanted = maxNames - names;
            IReadOnlyList<ResourceInfo> page = await GetResourcePageAsync(resourceContainerName, null, afterName, wanted,
                cancellationToken).ConfigureAwait(false);
            foreach (ResourceInfo current in page)
            {
                // A name deleted since it was listed has no versions left, and the next name takes its place.
                IReadOnlyList<ResourceInfo> ofName = await GetResourceVersionsAsync(
                    new ResourceLocation(resourceContainerName, current.Name), cancellationToken).ConfigureAwait(false);
                if (ofName.Count > 0) names++;
                versions.AddRange(ofName);
                afterName = current.Name;
            }
            if (page.Count < wanted) break;
        }
        return versions;
    }

    /// <summary>
    /// Gets one version of a resource, or the current version when <paramref name="versionId"/> is null, with
    /// <see cref="ResourceInfo.IsLatest"/> set accurately.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version, or <see langword="null"/> for the current version.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The version, or <see langword="null"/> when it does not exist.</returns>
    public abstract Task<ResourceInfo?> GetResourceInfoAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the content of one version of a resource, or of the current version when <paramref name="versionId"/>
    /// is null, as a read-only, forward-only stream: <see cref="Stream.Length"/> is the content's full length,
    /// <see cref="Stream.Position"/> the bytes read so far, and seeking, setting the position, writing and
    /// <see cref="Stream.SetLength"/> throw <see cref="NotSupportedException"/>. The caller owns and disposes it.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version to open, or <see langword="null"/> for the current version.</param>
    /// <param name="cancellationToken">Cancels opening the content.</param>
    /// <returns>The content, or <see langword="null"/> when the version does not exist.</returns>
    public abstract Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the content of a version as the other overload does, but starting at <paramref name="offset"/>, for a
    /// download of a range: <see cref="Stream.Length"/> is still the content's full length, and
    /// <see cref="Stream.Position"/> starts at <paramref name="offset"/>, or at the content's length when
    /// <paramref name="offset"/> is past it. Reading may go on past the range, so the client reads only
    /// <paramref name="length"/> bytes; a provider can stop reading storage there, as the SQL Server provider does.
    /// The default implementation opens the whole content and reads past the bytes before
    /// <paramref name="offset"/>, which costs as much as reading them: override it if the store can start at an offset.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version to open, or <see langword="null"/> for the current version.</param>
    /// <param name="offset">Where the range starts; not negative.</param>
    /// <param name="length">How many bytes the client will read, not negative; to the end when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels opening the content.</param>
    /// <returns>The content, or <see langword="null"/> when the version does not exist.</returns>
    public virtual async Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId, long offset,
        long? length, CancellationToken cancellationToken = default)
    {
        Stream? content = await GetResourceStreamAsync(resource, versionId, cancellationToken).ConfigureAwait(false);
        if (content is null) return null;
        try
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Clamp(offset, 1, 81920));
            try
            {
                for (long skipped = 0, read = 1; skipped < offset && read > 0; skipped += read)
                    read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset - skipped)), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return content;
        }
        catch
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deletes one version of a resource, or with no <paramref name="versionId"/> every version of it, if the name's
    /// current version meets <paramref name="conditions"/>, decided by the delete itself; a name with no current version
    /// meets no condition. Deleting the current version alone makes the most recently stored remaining version current.
    /// Deleting every version may happen in several steps, oldest first and the current version last: if it's canceled
    /// or fails partway, every version it didn't reach is still whole, the name still has its current version, and
    /// calling it again finishes the job. A version stored while it runs may remain.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version to delete, or <see langword="null"/> for every version.</param>
    /// <param name="conditions">What the name's current version must be like; <see langword="null"/> for no condition.</param>
    /// <param name="cancellationToken">Cancels the delete; a canceled delete of one version deletes nothing.</param>
    /// <returns><see langword="true"/> if anything was deleted; <see langword="false"/> if there was nothing to delete,
    /// including when a concurrent delete removed it first.</returns>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <paramref name="conditions"/>, or the name doesn't exist; nothing was deleted.
    /// </exception>
    public abstract Task<bool> DeleteResourceAsync(ResourceLocation resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default);

    // Resource Containers

    /// <summary>
    /// Adds a container, storing a copy of <paramref name="metadata"/>. Like <see cref="AddResourceAsync"/>, a duplicate
    /// is detected by the write itself.
    /// </summary>
    /// <param name="resourceContainerName">The container's name.</param>
    /// <param name="metadata">Its metadata.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ResourceConflictException">A container with the same name already exists.</exception>
    public abstract Task AddContainerAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists up to <paramref name="pageSize"/> containers, each with its own copy of the stored metadata, ordered by name
    /// in ordinal order (<see cref="StringComparer.Ordinal"/>). As with <see cref="GetResourcePageAsync"/>, a provider
    /// may use its storage's binary order instead, as long as its ordering and its <paramref name="afterName"/>
    /// comparison agree, and should answer without reading every container.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="prefix">Only names that start with this, ordinally; <see langword="null"/> for every name.</param>
    /// <param name="afterName">Only names after this one, in the listing's order; <see langword="null"/> to start from the first.</param>
    /// <param name="pageSize">The most containers to return.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The containers; empty when there are no more.</returns>
    public abstract Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName, int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a container, with its own copy of the stored metadata.
    /// </summary>
    /// <param name="resourceContainerName">The container's name.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The container, or <see langword="null"/> when it does not exist.</returns>
    public abstract Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a container and every resource and version in it. A provider may do this in several steps for a large
    /// container: if it is canceled or fails partway, every version it hasn't deleted must still be whole, each
    /// remaining name must still have exactly one current version, and calling it again must finish the job.
    /// </summary>
    /// <param name="resourceContainerName">The container's name.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> if the container existed and was deleted; otherwise <see langword="false"/>.</returns>
    public abstract Task<bool> DeleteContainerAsync(string resourceContainerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a container's <see cref="ContainerInfo.Metadata"/> with a copy of <paramref name="metadata"/>.
    /// An import uses it to mark the container it's filling as unfinished, and to clear the mark once it's done. The
    /// default implementation throws <see cref="NotSupportedException"/>, and imports then go unmarked.
    /// </summary>
    /// <param name="resourceContainerName">The container.</param>
    /// <param name="metadata">Its new metadata.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the container exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't change container metadata, as by default.</exception>
    public virtual Task<bool> SetContainerMetadataAsync(string resourceContainerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't change container metadata.");

    /// <summary>
    /// Stores a copy of one version of a resource, or of the current version when <paramref name="sourceVersionId"/> is
    /// null, as a new version described by <paramref name="destination"/>, with the source's content and hash. It's
    /// stored as <see cref="AddResourceAsync"/> stores an upload, deciding conflicts and conditions at the destination
    /// the same way, and keeps <see cref="NewResourceVersion.CopiedFrom"/>. The default implementation reads the content
    /// and stores it through that method; a provider that can copy content within its store should override it.
    /// </summary>
    /// <param name="source">The resource to copy.</param>
    /// <param name="sourceVersionId">The version to copy, or <see langword="null"/> for the current version.</param>
    /// <param name="destination">The new version: its location, version ID, type, metadata and origin.</param>
    /// <param name="condition">How to treat the destination name's current version, as for an upload.</param>
    /// <param name="cancellationToken">Cancels the copy; a canceled copy stores nothing.</param>
    /// <returns>What was stored, or <see langword="null"/> when the source version doesn't exist.</returns>
    public virtual async Task<WrittenContent?> CopyResourceAsync(ResourceLocation source, string? sourceVersionId,
        NewResourceVersion destination, ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default)
    {
        Stream? content = await GetResourceStreamAsync(source, sourceVersionId, cancellationToken).ConfigureAwait(false);
        if (content is null) return null;
        await using var contentDisposal = content.ConfigureAwait(false);
        return await AddResourceAsync(destination, content, condition, cancellationToken).ConfigureAwait(false);
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
    public virtual Task<bool> MoveResourceAsync(ResourceLocation source, ResourceLocation destination,
        ResourceConditions? sourceConditions, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't move resources.");

    /// <summary>
    /// Stores a version as it was in another store, for an import: with <paramref name="version"/>'s
    /// <see cref="NewResourceVersion.VersionId"/> and <see cref="NewResourceVersion.LastModified"/> rather than new ones,
    /// and its type, metadata, <see cref="NewResourceVersion.CopiedFrom"/> and <see cref="NewResourceVersion.ExpiresAt"/>.
    /// Otherwise it's stored as <see cref="AddResourceAsync"/> stores an upload: measured and hashed, refused if
    /// <see cref="NewResourceVersion.DeclaredLength"/> doesn't match, and made the name's current version. The default
    /// implementation throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="version">The version as it was; its <see cref="NewResourceVersion.LastModified"/> is set.</param>
    /// <param name="content">Its content, read to its end before the version is stored, as for an upload.</param>
    /// <param name="cancellationToken">Cancels the write; a canceled write stores nothing.</param>
    /// <returns>What was stored.</returns>
    /// <exception cref="ResourceConflictException">The name already has a version with that ID.</exception>
    /// <exception cref="NotSupportedException">The provider doesn't import versions.</exception>
    public virtual Task<WrittenContent> ImportVersionAsync(NewResourceVersion version, Stream content,
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
    /// The expiry and version ID of the previous batch's last version, to list the ones after it in that order;
    /// <see langword="null"/> to start from the first. A sweep goes through every expired version once this way, whatever it skips or fails to delete.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <exception cref="NotSupportedException">The provider doesn't support expiry.</exception>
    public virtual Task<IReadOnlyList<ResourceInfo>> GetExpiredAsync(DateTimeOffset now, int maxVersions,
        (DateTimeOffset ExpiresAt, string VersionId)? after = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Deletes one version, as <see cref="DeleteResourceAsync"/> does, only if its <see cref="ResourceInfo.ExpiresAt"/>
    /// is still at or before <paramref name="now"/>, decided by the delete itself: <see cref="SetExpiryAsync"/> may have
    /// changed it since it was listed.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version.</param>
    /// <param name="now">The time to decide expiry by.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> if it was deleted; <see langword="false"/> if it's gone or no longer expired.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support expiry, as by default.</exception>
    public virtual Task<bool> DeleteExpiredVersionAsync(ResourceLocation resource, string versionId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Deletes what uploads that stopped before finishing left behind, for a provider that stores a version's content
    /// before it makes the version visible, as SQL Server's does: anything such an upload last added to before
    /// <paramref name="staleBefore"/>. The expiry sweep calls it. The default implementation, for a provider that leaves
    /// nothing behind, does nothing.
    /// </summary>
    /// <param name="staleBefore">
    /// Leave alone what an upload added to at or after this time: it may still be running.
    /// </param>
    /// <param name="cancellationToken">Cancels the deletes; what's already deleted stays deleted.</param>
    /// <returns>How many uploads' leftovers it deleted.</returns>
    public virtual Task<int> DeleteAbandonedUploadsAsync(DateTimeOffset staleBefore, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    // Upload sessions: a staged version that content is appended to over several calls, which may come from different
    // processes, and that becomes current when it's completed. A staged version is invisible to every other member,
    // as an upload's is while its content arrives, and DeleteAbandonedUploadsAsync deletes one that nothing was
    // appended to since its cutoff. A provider supports them by overriding all six members; by default each throws
    // NotSupportedException.

    /// <summary>
    /// Stages <paramref name="version"/>, with no content, so content can be appended to it. It keeps the version's
    /// content type, metadata, expiry and declared length, for <see cref="GetStagedVersionAsync"/> to return.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="version">The version to stage.</param>
    /// <param name="cancellationToken">Cancels the start; a canceled start stages nothing.</param>
    /// <exception cref="ResourceContainerNotFoundException">The container doesn't exist.</exception>
    /// <exception cref="NotSupportedException">The provider doesn't support upload sessions, as by default.</exception>
    public virtual Task StartStagedVersionAsync(NewResourceVersion version, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support upload sessions.");

    /// <summary>
    /// A staged version as it stands: the version as it was staged, and how many bytes have been appended to it.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resource">The name the version is staged under.</param>
    /// <param name="versionId">The staged version's ID.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The staged version, or <see langword="null"/> if there's none: it was completed, deleted or swept.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support upload sessions, as by default.</exception>
    public virtual Task<StagedVersion?> GetStagedVersionAsync(ResourceLocation resource, string versionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support upload sessions.");

    /// <summary>
    /// Appends <paramref name="content"/> to a staged version, reading it to its end, provided the stored content ends
    /// at <paramref name="offset"/>. Content may be stored as it arrives, so an append that fails partway keeps what it
    /// stored, and the caller resumes from the new length. Each part stored moves the version's stamp forward, so
    /// <see cref="DeleteAbandonedUploadsAsync"/> leaves a session alone while content still arrives.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resource">The name the version is staged under.</param>
    /// <param name="versionId">The staged version's ID.</param>
    /// <param name="offset">Where the content starts: the stored length the caller last saw.</param>
    /// <param name="content">The content to append.</param>
    /// <param name="cancellationToken">Cancels the append; what's already stored stays stored.</param>
    /// <returns>The stored length after the append, or <see langword="null"/> if there's no such staged version.</returns>
    /// <exception cref="ResourceUploadOffsetException">The stored content doesn't end at <paramref name="offset"/>.</exception>
    /// <exception cref="ResourceUploadLimitException">
    /// The session already holds as much as the provider can store; what it stored stays.
    /// </exception>
    /// <exception cref="NotSupportedException">The provider doesn't support upload sessions, as by default.</exception>
    public virtual Task<long?> AppendStagedContentAsync(ResourceLocation resource, string versionId, long offset, Stream content,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support upload sessions.");

    /// <summary>
    /// Reads a staged version's content from the start, for the clients to measure, hash and inspect it before it's
    /// completed. The caller owns and disposes the stream.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resource">The name the version is staged under.</param>
    /// <param name="versionId">The staged version's ID.</param>
    /// <param name="cancellationToken">Cancels opening the stream.</param>
    /// <returns>The content, or <see langword="null"/> if there's no such staged version.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support upload sessions, as by default.</exception>
    public virtual Task<Stream?> GetStagedStreamAsync(ResourceLocation resource, string versionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support upload sessions.");

    /// <summary>
    /// Makes a staged version its name's current version, with the length and hash the clients measured, as
    /// <see cref="AddResourceAsync"/> makes an upload current: deciding <paramref name="condition"/> in the same step,
    /// and stamping the version with the time it becomes current.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resource">The name the version is staged under.</param>
    /// <param name="versionId">The staged version's ID.</param>
    /// <param name="contentLength">The content's length, as the clients measured it.</param>
    /// <param name="contentHash">The content's hash, as the clients measured it.</param>
    /// <param name="condition">What the name's current version must be for the version to become current.</param>
    /// <param name="cancellationToken">Cancels the completion; a canceled completion leaves the version staged.</param>
    /// <returns>
    /// What was stored, stamped with the time it became current, or <see langword="null"/> if there's no such staged
    /// version.
    /// </returns>
    /// <exception cref="ResourceConflictException">The condition is create-only and the name has a current version.</exception>
    /// <exception cref="ResourcePreconditionFailedException">The name's current version doesn't meet the conditions.</exception>
    /// <exception cref="ResourceUploadOffsetException">
    /// The stored content isn't <paramref name="contentLength"/> long: something was appended after it was measured.
    /// </exception>
    /// <exception cref="NotSupportedException">The provider doesn't support upload sessions, as by default.</exception>
    public virtual Task<WrittenContent?> CompleteStagedVersionAsync(ResourceLocation resource, string versionId, long contentLength,
        string contentHash, ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support upload sessions.");

    /// <summary>
    /// Deletes a staged version and its content.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resource">The name the version is staged under.</param>
    /// <param name="versionId">The staged version's ID.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> if it was deleted; <see langword="false"/> if there's no such staged version.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support upload sessions, as by default.</exception>
    public virtual Task<bool> DeleteStagedVersionAsync(ResourceLocation resource, string versionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support upload sessions.");

    /// <summary>
    /// Deletes each of <paramref name="versions"/> that's still expired at <paramref name="now"/>, as
    /// <see cref="DeleteExpiredVersionAsync"/> does for one, and returns those it deleted; a version that's
    /// gone, or no longer expired, is left out. A sweep deletes each page of expired versions it lists through it. A
    /// provider should answer it in a few round trips; the default implementation calls
    /// <see cref="DeleteExpiredVersionAsync"/> for each.
    /// </summary>
    /// <param name="versions">The versions to delete if they're still expired.</param>
    /// <param name="now">The time to decide expiry by.</param>
    /// <param name="cancellationToken">Cancels the deletes; the versions already deleted stay deleted.</param>
    /// <returns>The versions it deleted.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support expiry.</exception>
    public virtual async Task<IReadOnlyCollection<(ResourceLocation Resource, string VersionId)>> DeleteExpiredVersionsAsync(
        IReadOnlyList<(ResourceLocation Resource, string VersionId)> versions, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var deleted = new List<(ResourceLocation, string)>();
        foreach ((ResourceLocation resource, string versionId) in versions)
        {
            if (await DeleteExpiredVersionAsync(resource, versionId, now, cancellationToken).ConfigureAwait(false))
                deleted.Add((resource, versionId));
        }
        return deleted;
    }

    /// <summary>
    /// Sets one version's <see cref="ResourceInfo.ExpiresAt"/>, or the current version's when <paramref name="versionId"/>
    /// is <see langword="null"/>; an <paramref name="expiresAt"/> of <see langword="null"/> clears it. It changes nothing
    /// else about the version. <see cref="AddResourceAsync"/> and <see cref="CopyResourceAsync"/> store the expiry from
    /// the version they're given, and every read returns it.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version, or <see langword="null"/> for the current one.</param>
    /// <param name="expiresAt">When it expires; <see langword="null"/> never.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't support expiry, as by default.</exception>
    public virtual Task<bool> SetExpiryAsync(ResourceLocation resource, string? versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Replaces one version's <see cref="ResourceInfo.Metadata"/> with a copy of <paramref name="metadata"/>, changing
    /// nothing else about it.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version; <see langword="null"/> for the current one.</param>
    /// <param name="metadata">The version's new metadata.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't change a version's metadata, as by default.</exception>
    public virtual Task<bool> SetResourceMetadataAsync(ResourceLocation resource, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't change a version's metadata.");

    /// <summary>
    /// Sets one version's <see cref="ResourceInfo.ContentType"/>, or clears it with <see langword="null"/>, changing
    /// nothing else about it.
    /// </summary>
    /// <param name="resource">The resource.</param>
    /// <param name="versionId">The version; <see langword="null"/> for the current one.</param>
    /// <param name="contentType">The version's new content type.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="NotSupportedException">The provider doesn't change a version's content type, as by default.</exception>
    public virtual Task<bool> SetContentTypeAsync(ResourceLocation resource, string? versionId, string? contentType,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't change a version's content type.");

    /// <summary>
    /// Gets what a container holds: the total <see cref="ResourceInfo.ContentLength"/> of every version, and how many
    /// resources and versions there are. Container policies read it before each write they limit, so a provider should
    /// answer it without reading content. The default implementation lists every resource's versions, one name at a
    /// time; override it if the store can add them up itself.
    /// </summary>
    /// <param name="resourceContainerName">The container.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The container's usage; nothing stored when the container doesn't exist.</returns>
    public virtual async Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ResourceInfo> resources = await GetAllResourceInfoAsync(resourceContainerName, cancellationToken).ConfigureAwait(false);
        long storedBytes = 0, versionCount = 0;
        foreach (ResourceInfo resource in resources)
        {
            foreach (ResourceInfo version in await GetResourceVersionsAsync(
                new ResourceLocation(resourceContainerName, resource.Name), cancellationToken).ConfigureAwait(false))
            {
                storedBytes += version.ContentLength ?? 0;
                versionCount++;
            }
        }

        return new ResourceContainerUsage(storedBytes, resources.Count, versionCount);
    }

    /// <summary>
    /// Lists up to <paramref name="count"/> of a container's versions, current and older, in <paramref name="order"/>,
    /// each with <see cref="ResourceInfo.IsLatest"/> set: the largest first, or the ones that expire soonest first,
    /// leaving out versions without an expiry. Ties are in version ID order, ordinally, and with
    /// <see cref="ResourceVersionOrder.LargestFirst"/>, a version without a <see cref="ResourceInfo.ContentLength"/> comes
    /// after every version with one. It's for showing an administrator what fills a container or expires next, so it
    /// needn't be fast, but a provider that can should answer it in one query. The default implementation reads every
    /// version through <see cref="GetVersionPageAsync"/>, keeping the top <paramref name="count"/>.
    /// </summary>
    /// <param name="resourceContainerName">The container.</param>
    /// <param name="order">Which versions come first.</param>
    /// <param name="count">The most versions to return, from 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The versions; empty when there are none, or the container does not exist.</returns>
    public virtual async Task<IReadOnlyList<ResourceInfo>> GetTopVersionsAsync(string resourceContainerName,
        ResourceVersionOrder order, int count, CancellationToken cancellationToken = default)
    {
        IComparer<ResourceInfo> ranking = Ranking(order);
        // The kept version that ranks last is on top, so a version that ranks below it is dropped after one comparison.
        var kept = new PriorityQueue<ResourceInfo, ResourceInfo>(count, Comparer<ResourceInfo>.Create((x, y) => ranking.Compare(y, x)));
        string? afterName = null;
        while (true)
        {
            IReadOnlyList<ResourceInfo> page = await GetVersionPageAsync(resourceContainerName, afterName,
                ResourceListOptions.DefaultPageSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            foreach (ResourceInfo version in page)
            {
                if (order == ResourceVersionOrder.ExpiringFirst && version.ExpiresAt is null) continue;
                if (kept.Count < count) kept.Enqueue(version, version);
                else kept.EnqueueDequeue(version, version);
            }
            afterName = page[^1].Name;
        }
        return [.. kept.UnorderedItems.Select(item => item.Element).Order(ranking)];
    }

    /// <summary>The order <see cref="GetTopVersionsAsync"/> lists versions in, for versions it lists.</summary>
    private static IComparer<ResourceInfo> Ranking(ResourceVersionOrder order) => order == ResourceVersionOrder.LargestFirst
        ? Comparer<ResourceInfo>.Create((x, y) => (x.ContentLength, y.ContentLength) switch
        {
            (null, not null) => 1,
            (not null, null) => -1,
            ({ } a, { } b) when a != b => b.CompareTo(a),
            _ => string.CompareOrdinal(x.VersionId, y.VersionId)
        })
        : Comparer<ResourceInfo>.Create((x, y) => x.ExpiresAt != y.ExpiresAt
            ? x.ExpiresAt!.Value.CompareTo(y.ExpiresAt!.Value)
            : string.CompareOrdinal(x.VersionId, y.VersionId));
}
