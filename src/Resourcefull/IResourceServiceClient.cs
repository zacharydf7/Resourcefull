namespace Resourcefull;

/// <summary>
/// The entry point for working with resource containers. Mirrors the shape of Azure Blob Storage's
/// <c>BlobServiceClient</c> so usage and a future migration feel familiar.
/// </summary>
/// <remarks>
/// Every operation that contacts the store has an overload that takes a <see cref="CancellationToken"/> as
/// its last parameter. A cancelled operation throws <see cref="OperationCanceledException"/> and never
/// returns a result.
/// </remarks>
public interface IResourceServiceClient
{
    /// <summary>
    /// Returns a client for a container without contacting the store. The container need not exist
    /// yet; operations report absence at the time they run. This is the cheap, lazy equivalent of
    /// Azure's <c>GetBlobContainerClient</c>.
    /// </summary>
    /// <param name="resourceContainerName">The name of the resource container.</param>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public IResourceContainerClient GetResourceContainerClient(string resourceContainerName);

    /// <summary>
    /// Creates a new resource container.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container to create.</param>
    /// <param name="metadata">Optional metadata to store with the container.</param>
    /// <returns>A client for the newly created container.</returns>
    /// <exception cref="ResourceConflictException">A container with the same name already exists.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<IResourceContainerClient> CreateResourceContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata = null);

    /// <inheritdoc cref="CreateResourceContainerAsync(string, IDictionary{string, string}?)"/>
    /// <param name="cancellationToken">Cancels the operation. A cancelled create leaves no container.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<IResourceContainerClient> CreateResourceContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a resource container if it does not already exist, returning a client for it either way.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container.</param>
    /// <param name="metadata">Optional metadata applied only when the container is created.</param>
    /// <returns>A client for the container.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<IResourceContainerClient> CreateResourceContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata = null);

    /// <inheritdoc cref="CreateResourceContainerIfNotExistsAsync(string, IDictionary{string, string}?)"/>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<IResourceContainerClient> CreateResourceContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the attributes of a container.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container.</param>
    /// <returns>The <see cref="ResourceContainerInfo"/>, or <see langword="null"/> if the container does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName);

    /// <inheritdoc cref="GetResourceContainerInfoAsync(string)"/>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the attributes of all resource containers.
    /// </summary>
    /// <returns>A (possibly empty) list of container attributes.</returns>
    public Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync();

    /// <inheritdoc cref="GetResourceContainersAsync()"/>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<IReadOnlyList<ResourceContainerInfo>> GetResourceContainersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a container exists.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container.</param>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<bool> ContainerExistsAsync(string resourceContainerName);

    /// <inheritdoc cref="ContainerExistsAsync(string)"/>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<bool> ContainerExistsAsync(string resourceContainerName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a container (and everything in it) if it exists. A large container may be deleted in several steps: if
    /// the delete is cancelled or fails partway, every version it didn't reach is still whole, each remaining name still
    /// has its current version, and the container still exists, so calling it again finishes the job.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container to delete.</param>
    /// <returns><see langword="true"/> if a container was deleted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<bool> DeleteResourceContainerIfExistsAsync(string resourceContainerName);

    /// <inheritdoc cref="DeleteResourceContainerIfExistsAsync(string)"/>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<bool> DeleteResourceContainerIfExistsAsync(string resourceContainerName, CancellationToken cancellationToken);
}
