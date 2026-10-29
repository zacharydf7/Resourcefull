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
    /// <param name="stream">The content, read once from its current position (rewound first when it can seek).</param>
    /// <param name="createOnly">Store only if no version of the name exists.</param>
    /// <param name="cancellationToken">Cancels the write; a cancelled write stores nothing.</param>
    /// <returns>
    /// The length and hash of what was stored, measured by reading <paramref name="stream"/> through a
    /// <see cref="HashingStream"/>; the measured length, not <see cref="ResourceInfo.ContentLength"/>, is stored.
    /// </returns>
    /// <exception cref="ResourceContentLengthMismatchException">
    /// <paramref name="resourceInfo"/> declares a content length that differs from the stream; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceContainerNotFoundException">The named container does not exist.</exception>
    /// <exception cref="ResourceConflictException"><paramref name="createOnly"/> is set and the name already exists.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; nothing was stored.</exception>
    Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the current version of every resource in a container, each with <see cref="ResourceInfo.IsLatest"/> set.
    /// </summary>
    /// <returns>The resources; empty when the container is empty or does not exist.</returns>
    Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default);

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

    // Resource Containers

    /// <summary>
    /// Adds a container, storing its <see cref="ResourceContainerInfo.Uri"/> and a copy of its
    /// <see cref="ResourceContainerInfo.Metadata"/>. Like <see cref="AddResourceAsync"/>, a duplicate is detected by
    /// the write itself.
    /// </summary>
    /// <exception cref="ResourceConflictException">A container with the same name already exists.</exception>
    Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every container, each with its stored URI and its own copy of the stored metadata.
    /// </summary>
    Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a container, with its stored URI and its own copy of the stored metadata.
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
