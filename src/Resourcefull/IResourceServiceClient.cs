using System.Runtime.CompilerServices;

namespace Resourcefull;

/// <summary>
/// The entry point for working with resource containers. Inject it, then get an <see cref="IResourceContainerClient"/>
/// for the resources in one container.
/// </summary>
/// <remarks>
/// Every operation that contacts the store takes an optional <see cref="CancellationToken"/> as its last
/// parameter. A canceled operation throws <see cref="OperationCanceledException"/> and never
/// returns a result.
/// </remarks>
public interface IResourceServiceClient
{
    /// <summary>
    /// Returns a client for a container without contacting the store. The container need not exist
    /// yet; operations report absence at the time they run. Since nothing is read, the client's
    /// <see cref="IResourceContainerClient.Info"/> carries the name and the URI provider's URI but no
    /// metadata; use <see cref="GetContainerInfoAsync(string, CancellationToken)"/> for the stored metadata.
    /// </summary>
    /// <param name="resourceContainerName">The name of the resource container.</param>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public IResourceContainerClient GetContainerClient(string resourceContainerName);

    /// <summary>
    /// Creates a new resource container.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container to create.</param>
    /// <param name="metadata">Optional metadata to store with the container.</param>
    /// <param name="cancellationToken">Cancels the operation. A canceled create leaves no container.</param>
    /// <returns>A client for the newly created container.</returns>
    /// <exception cref="ResourceConflictException">A container with the same name already exists.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<IResourceContainerClient> CreateContainerAsync(string resourceContainerName, IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a resource container if it does not already exist, returning a client for it either way.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container.</param>
    /// <param name="metadata">Optional metadata applied only when the container is created.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A client for the container.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<IResourceContainerClient> CreateContainerIfNotExistsAsync(string resourceContainerName, IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the attributes of a container.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The <see cref="ContainerInfo"/>, or <see langword="null"/> if the container does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every container, or those whose names start with <see cref="ContainerListOptions.Prefix"/>, ordered by
    /// name, fetching a page at a time as you enumerate. A container that exists for the whole listing appears exactly
    /// once; one created or deleted during it may or may not.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="options">The prefix and page size; <see langword="null"/> lists everything, 1,000 at a time.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The containers, ordered by name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ContainerListOptions.PageSize"/> is outside 1 to <see cref="ContainerListOptions.MaxPageSize"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <remarks>
    /// The default implementation fetches the pages through <see cref="GetContainerPageAsync"/>.
    /// </remarks>
    public async IAsyncEnumerable<ContainerInfo> ListContainersAsync(ContainerListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            ContainerPage page = await GetContainerPageAsync(options, continuationToken, cancellationToken).ConfigureAwait(false);
            foreach (ContainerInfo container in page.Containers) yield return container;
            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);
    }

    /// <summary>
    /// Gets one page of the containers, ordered by name, for an API that hands the continuation token to its own
    /// callers. Each page is a query of its own, so there's no snapshot: a container that exists for the whole listing
    /// appears exactly once, and one created or deleted during it may or may not.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="options">The prefix and page size; <see langword="null"/> lists everything, 1,000 at a time.</param>
    /// <param name="continuationToken">
    /// The previous page's <see cref="ContainerPage.ContinuationToken"/>, or <see langword="null"/> for the first page.
    /// It must come from a listing with the same prefix; the page size can change.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page, and the token for the next one if there is one.</returns>
    /// <exception cref="ArgumentException"><paramref name="continuationToken"/> is malformed or comes from a listing of another prefix.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ContainerListOptions.PageSize"/> is outside 1 to <see cref="ContainerListOptions.MaxPageSize"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ContainerPage> GetContainerPageAsync(ContainerListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a container exists.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<bool> ContainerExistsAsync(string resourceContainerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a container (and everything in it) if it exists. A large container may be deleted in several steps: if
    /// the delete is canceled or fails partway, every version it didn't reach is still whole, each remaining name still
    /// has its current version, and the container still exists, so calling it again finishes the job.
    /// </summary>
    /// <param name="resourceContainerName">The name of the container to delete.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> if a container was deleted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> is null, empty, longer than 128 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only; nothing was deleted.</exception>
    /// <exception cref="ResourceRetainedException">
    /// A version in the container is retained or under a legal hold. Nothing was deleted, unless the version was
    /// protected while a batched delete was under way (SQL Server, PostgreSQL and SQLite delete containers in batches).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<bool> DeleteContainerIfExistsAsync(string resourceContainerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a container to <paramref name="destination"/> as a zip, to back it up or to move it to another store:
    /// <c>manifest.json</c>, which describes the container and every version of every resource, and each distinct
    /// content once, under <c>content/</c>. It reads one version at a time, and can write to a stream that can't seek,
    /// such as an HTTP response.
    /// <para>Coming in 4.0.</para>
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
    /// <para>Coming in 4.0.</para>
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
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the cleanup; the versions already deleted stay deleted.</param>
    /// <returns>How many versions were deleted.</returns>
    /// <exception cref="AggregateException">
    /// Some versions weren't deleted, because the cleanup handler or the delete failed; the others were, and those stay
    /// for the next call. Its message counts them all, and it holds the first few failures.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The provider broke its <c>GetExpiredAsync</c> contract: it listed the same expired versions twice, or listed one
    /// without an expiry.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support expiry, or this is the default implementation, kept so implementations written for
    /// 3.1 still compile.
    /// </exception>
    public Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Lists versions, in every container, whose <see cref="ResourceInfo.ExpiresAt"/> has passed but that
    /// <see cref="DeleteExpiredAsync"/> hasn't deleted yet, each with <see cref="ResourceInfo.IsLatest"/> set: the ones that
    /// expired first come first, and those that expired at the same time are in version ID order. Use it to see whether
    /// the expiry sweep is keeping up. A read-only container's expired versions are listed too, since the sweep keeps
    /// them until the container is writable again.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="maxVersions">The most versions to return, from 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The versions; empty when none are waiting.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxVersions"/> is outside 1 to 1,000.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support expiry, or this is the default implementation.
    /// </exception>
    public Task<IReadOnlyList<ResourceInfo>> GetExpiredVersionsAsync(int maxVersions = 100, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't support expiry.");

    /// <summary>
    /// Prunes every resource in the container to its <see cref="ResourceContainerPolicy.MaxVersions"/>,
    /// as each upload prunes its own name, telling the <see cref="IResourceCleanupHandler"/> as a prune does. Run it after
    /// an import, which keeps every version it was given, or after a prune failed and its name hasn't been written since.
    /// With <see cref="ResourcefullOptionsBuilder.UseExpiry"/>, the versions past the limit are set to expire five
    /// minutes later rather than deleted at once, as a prune does.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container to prune; a container that doesn't exist has nothing to prune.</param>
    /// <param name="cancellationToken">Stops the pruning; the versions already pruned stay pruned.</param>
    /// <returns>How many versions were past the limit.</returns>
    /// <exception cref="AggregateException">
    /// Some resources weren't pruned, because the cleanup handler or the provider failed for them; the others were, and
    /// the failed ones keep their extra versions until a later prune. Its message counts them, and it holds the first few
    /// failures.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceContainerName"/> breaks the name rules (<see cref="ResourceNameException"/>), or is null
    /// (<see cref="ArgumentNullException"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// This default implementation, kept so implementations written for 3.1 still compile, doesn't prune.
    /// </exception>
    public Task<int> PruneContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't prune containers.");

    /// <summary>
    /// Indexes the keys <see cref="ResourcefullOptionsBuilder.UseMetadataSearch"/> names for every current version in
    /// the container, so a listing by <see cref="ResourceListOptions.Metadata"/> finds versions stored before a key was
    /// added. New writes are indexed as they're stored, so run it once after adding a key, for each container that
    /// holds versions from before. With a provider that keeps no index, it does nothing, since searches read every name.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container to index; a container that doesn't exist has nothing to index.</param>
    /// <param name="cancellationToken">Stops the indexing; what's indexed by then stays indexed, and running it again finishes.</param>
    /// <returns>How many versions were indexed.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceContainerName"/> breaks the name rules (<see cref="ResourceNameException"/>), or is null
    /// (<see cref="ArgumentNullException"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">This default implementation doesn't index metadata.</exception>
    public Task<int> IndexMetadataAsync(string resourceContainerName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't index metadata.");

    /// <summary>
    /// Replaces a container's metadata, which otherwise is set only when the container is created. The whole dictionary
    /// is replaced, including the <c>resourcefull.import</c> entry that marks an unfinished import; to change one entry,
    /// read the container's metadata, change it and set it. An import running at the same time replaces the metadata
    /// again when it finishes.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container.</param>
    /// <param name="metadata">The container's new metadata, which is copied.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the container exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't change container metadata, or this is the default implementation, kept so implementations
    /// written for 3.1 still compile.
    /// </exception>
    public Task<bool> SetContainerMetadataAsync(string resourceContainerName, IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't change container metadata.");
}
