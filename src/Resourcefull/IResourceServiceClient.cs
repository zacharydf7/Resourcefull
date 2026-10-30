// The compiler doesn't expand <inheritdoc cref>, so it reports the parameters each CancellationToken overload
// inherits as undocumented.
#pragma warning disable CS1573

namespace Resourcefull;

/// <summary>
/// The entry point for working with resource containers. Inject it, then get an <see cref="IResourceContainerClient"/>
/// for the resources in one container.
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
    /// yet; operations report absence at the time they run. Since nothing is read, the client's
    /// <see cref="IResourceContainerClient.ResourceContainerInfo"/> carries the name and the URI provider's URI but no
    /// metadata; use <see cref="GetResourceContainerInfoAsync(string)"/> for the stored metadata.
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

    /// <summary>
    /// Writes a container to <paramref name="destination"/> as a zip, to back it up or to move it to another store:
    /// <c>manifest.json</c>, which describes the container and every version of every resource, and each distinct
    /// content once, under <c>content/</c>. It reads one version at a time, and can write to a stream that can't seek,
    /// such as an HTTP response.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container to export.</param>
    /// <param name="destination">Where to write the zip; it's left open.</param>
    /// <param name="cancellationToken">Cancels the export, leaving <paramref name="destination"/> incomplete.</param>
    /// <returns>How many resources and versions it wrote, and how much content.</returns>
    /// <exception cref="ResourceContainerNotFoundException">The container doesn't exist.</exception>
    /// <exception cref="InvalidDataException">A version's stored content doesn't match its recorded hash.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> breaks the name rules, or <paramref name="destination"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// This default implementation, kept so implementations written for 3.1 still compile, doesn't export.
    /// </exception>
    public Task<ResourceExportResult> ExportContainerAsync(string resourceContainerName, Stream destination,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't export containers.");

    /// <summary>
    /// Creates a container from an export that <see cref="ExportContainerAsync"/> wrote, with every version as it was:
    /// its ID, <see cref="ResourceInfo.LastModified"/>, content and attributes. The manifest is checked before anything is
    /// created, each content is checked against its hash as it's stored, and the container's policy applies. If
    /// anything fails, the container is deleted again, so nothing is left half-imported. The hashes catch damage in
    /// storage or transfer, not deliberate edits, so import only exports you trust.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container to create, which mustn't exist.</param>
    /// <param name="source">The zip, in a stream that can seek, such as a file; it's left open.</param>
    /// <param name="cancellationToken">Cancels the import, which then deletes what it created.</param>
    /// <returns>How many resources and versions it stored, and how much content.</returns>
    /// <exception cref="ResourceConflictException">The container already exists.</exception>
    /// <exception cref="InvalidDataException">
    /// The zip isn't an export this release can read, describes something that can't be imported, or its content was
    /// damaged or changed.
    /// </exception>
    /// <exception cref="AggregateException">
    /// The import failed, and so did deleting what it had imported; it holds both failures. Delete the container before
    /// importing it again.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container's policy refuses a version.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceContainerName"/> breaks the name rules, or <paramref name="source"/> is null or can't seek.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't import versions, or this is the default implementation, kept so implementations written for
    /// 3.1 still compile.
    /// </exception>
    public Task<ResourceImportResult> ImportContainerAsync(string resourceContainerName, Stream source,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't import containers.");

    /// <summary>
    /// Deletes every version, in every container that isn't read-only, whose <see cref="ResourceInfo.ExpiresAt"/> has
    /// passed, as a delete of that version would, a batch at a time, telling the <see cref="IResourceCleanupHandler"/>
    /// about each one first. A read-only container keeps its expired versions until it's writable again.
    /// <see cref="ResourcefullOptionsBuilder.UseExpiry"/> calls it every interval; call it yourself to run expiry another way.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the cleanup; the versions already deleted stay deleted.</param>
    /// <returns>How many versions were deleted.</returns>
    /// <exception cref="AggregateException">
    /// Some versions weren't deleted, because the cleanup handler or the delete failed; the others were, and those stay
    /// for the next call. Its message counts them all, and it holds the first few failures.
    /// </exception>
    /// <exception cref="InvalidOperationException">The provider listed the same expired versions twice, against its contract.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support expiry, or this is the default implementation, kept so implementations written for
    /// 3.1 still compile.
    /// </exception>
    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");
}
