using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.SqlServer")]
[assembly: InternalsVisibleTo("Resourcefull.InMemory")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Integration")]

// Required for Moq
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace Resourcefull;

/// <summary>
/// Every member observes its <c>cancellationToken</c>: a cancelled operation throws
/// <see cref="OperationCanceledException"/> and never reports success or a partial result.
/// </summary>
internal interface IResourceRepository
{
    // Resources

    /// <summary>
    /// Adds a resource as the new current version of its name. Implementations must not silently discard the write
    /// when the container is absent, and must decide that from the write itself rather than a prior check. The same
    /// applies to <paramref name="createOnly"/>, and concurrent writers must never leave more than one current version.
    /// </summary>
    /// <param name="createOnly">Store only if no version of the name exists.</param>
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
    Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default);
    Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default);
    Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);
    Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// Deletes one version of a resource, or with no <paramref name="versionId"/> every version of it. Deleting the
    /// current version alone makes the most recently stored remaining version current.
    /// </summary>
    Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);

    // Resource Containers

    /// <summary>
    /// Adds a container. Like <see cref="AddResourceAsync"/>, a duplicate is detected by the write itself.
    /// </summary>
    /// <exception cref="ResourceConflictException">A container with the same name already exists.</exception>
    Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default);
    Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default);
    Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default);
    Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default);
}