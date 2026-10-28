using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.SqlServer")]
[assembly: InternalsVisibleTo("Resourcefull.InMemory")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

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
    /// Adds a resource to an existing container. Implementations must not silently discard the write
    /// when the container is absent, and must decide that from the write itself rather than a prior check.
    /// </summary>
    /// <exception cref="ResourceContainerNotFoundException">The named container does not exist.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; nothing was stored.</exception>
    Task AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, CancellationToken cancellationToken = default);
    Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default);
    Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default);
    Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);
    Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);
    Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default);

    // Resource Containers
    Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default);
    Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default);
    Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default);
    Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default);
}