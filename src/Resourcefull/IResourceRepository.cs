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
}
