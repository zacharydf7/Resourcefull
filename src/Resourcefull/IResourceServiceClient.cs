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
    /// The name of the store this client works with, given to
    /// <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/>, or <see langword="null"/> for the store
    /// <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> adds. A class that wraps a client returns
    /// the wrapped client's name, because signed links are checked with the signer of the store this names.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public string? StoreName { get; }

    /// <summary>
    /// Returns a client for a container without contacting the store. The container need not exist
    /// yet; operations report absence at the time they run. Use the client's
    /// <see cref="IResourceContainerClient.GetInfoAsync"/> for the container's stored attributes.
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
    /// The provider doesn't import versions, or the export has a version that expires and the provider doesn't support
    /// expiry, or a retained or held version and the provider doesn't support retention, or this is the default
    /// implementation, kept so implementations written for 3.1 still compile.
    /// </exception>
    public Task<ResourceImportResult> ImportContainerAsync(string resourceContainerName, Stream source,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't import containers.");

    /// <summary>
    /// Creates a container from versions another store holds, each as it was: its ID,
    /// <see cref="ResourceInfo.LastModified"/>, content and attributes. They're stored in the order given, each as its
    /// name's newest, so give each name's versions oldest first. Each content is checked against its hash as it's stored,
    /// and the container's policy applies. While it fills, the container's metadata marks it unfinished, with
    /// <c>resourcefull.import</c> set to <c>in-progress</c>, and the time in <c>resourcefull.import_at</c> is written again
    /// every five minutes, so a copy can tell an import still running from one that stopped. If anything fails, the
    /// container is deleted again. <see cref="CopyContainerToAsync"/> imports through it.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container to create, which mustn't exist.</param>
    /// <param name="versions">The versions to store, each name's oldest first.</param>
    /// <param name="metadata">The container's metadata; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the import, which then deletes what it created.</param>
    /// <returns>How many resources and versions it stored, and how much content.</returns>
    /// <exception cref="ResourceConflictException">The container already exists, or a name has two versions with one ID.</exception>
    /// <exception cref="InvalidDataException">A version's content doesn't match its hash.</exception>
    /// <exception cref="AggregateException">
    /// The import failed, and so did deleting what it had imported; it holds both failures. Delete the container before
    /// importing it again.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container's policy refuses a version.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> breaks the name rules, or <paramref name="versions"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't import versions, or a version expires and the provider doesn't support expiry, or a version
    /// is retained or held and the provider doesn't support retention, or this is the default implementation.
    /// </exception>
    public Task<ResourceImportResult> ImportContainerAsync(string resourceContainerName, IAsyncEnumerable<ImportedVersion> versions,
        IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't import containers.");

    /// <summary>
    /// Adds versions another store holds to a container that exists, each as it was, as
    /// <see cref="ImportContainerAsync(string, IAsyncEnumerable{ImportedVersion}, IReadOnlyDictionary{string, string}?, CancellationToken)"/>
    /// stores them: in the order given, each as its name's newest. A copy with
    /// <see cref="ExistingContainers.Update"/> brings a container up to date through it. Unlike an import into a new
    /// container, the versions stored before a failure stay.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container, which must exist.</param>
    /// <param name="versions">The versions to store, each name's oldest first.</param>
    /// <param name="cancellationToken">Cancels the import; the versions already stored stay.</param>
    /// <returns>How many resources and versions it stored, and how much content.</returns>
    /// <exception cref="ResourceContainerNotFoundException">The container doesn't exist.</exception>
    /// <exception cref="ResourceConflictException">A version's ID is already used by another version of its name, including one in the bin.</exception>
    /// <exception cref="InvalidDataException">A version's content doesn't match its hash.</exception>
    /// <exception cref="ResourcePolicyViolationException">The container's policy refuses a version.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceContainerName"/> breaks the name rules, or <paramref name="versions"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't import versions, or a version expires and the provider doesn't support expiry, or a version
    /// is retained or held and the provider doesn't support retention, or this is the default implementation.
    /// </exception>
    public Task<ResourceImportResult> ImportVersionsAsync(string resourceContainerName, IAsyncEnumerable<ImportedVersion> versions,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't import versions.");

    /// <summary>
    /// Copies a container to another store, such as one with another provider, every version of every resource as it
    /// was: version IDs, <see cref="ResourceInfo.LastModified"/>, content types, metadata, origins, expiries, retention
    /// dates and legal holds, and the container's metadata. It reads this store a page of names at a time, as an export
    /// does, and stores through the destination's
    /// <see cref="ImportContainerAsync(string, IAsyncEnumerable{ImportedVersion}, IReadOnlyDictionary{string, string}?, CancellationToken)"/>,
    /// so each content is checked against its hash as it's stored, the destination's policies apply, and a copy that
    /// fails deletes what it created. The destination's container is visible while it fills, marked unfinished. A
    /// version stored here after its name was read isn't copied; <see cref="CopyStoreToAsync"/> with
    /// <see cref="ExistingContainers.Update"/> catches up. Upload sessions in progress and the
    /// <see cref="ResourceContainerPolicy.KeepDeletedFor">bin</see> aren't copied.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resourceContainerName">The container to copy.</param>
    /// <param name="destination">The store to copy it to, which may be this one, for a copy under another name.</param>
    /// <param name="destinationContainerName">The name it gets there, which mustn't exist; <see langword="null"/> for its own.</param>
    /// <param name="cancellationToken">Cancels the copy, which then deletes what it created.</param>
    /// <returns>What it copied.</returns>
    /// <exception cref="ResourceContainerNotFoundException">The container doesn't exist here.</exception>
    /// <exception cref="ResourceConflictException">The destination already has a container with that name.</exception>
    /// <exception cref="ArgumentException">A name breaks the name rules, or <paramref name="destination"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// The destination can't import versions, or this is the default implementation.
    /// </exception>
    public Task<ContainerCopyResult> CopyContainerToAsync(string resourceContainerName, IResourceServiceClient destination,
        string? destinationContainerName = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't copy containers.");

    /// <summary>
    /// Copies every container, or those whose names start with <see cref="StoreCopyOptions.Prefix"/>, to another store,
    /// one at a time, as <see cref="CopyContainerToAsync"/> does. <see cref="StoreCopyOptions.Existing"/> decides what
    /// happens to a container the destination already has: whether it fails, is skipped once it's found to match version
    /// for version, or is brought up to date in place. A destination container still being filled by another copy or
    /// import fails, whatever <see cref="StoreCopyOptions.Existing"/> says; one whose copy stopped partway can be
    /// finished with <see cref="ExistingContainers.Update"/>. Run it again after it stops to go on from there.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="destination">The store to copy to.</param>
    /// <param name="options">The prefix, what to do with existing containers and failures, and progress; <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the copy. A container being copied is deleted again; those done stay.</param>
    /// <returns>What it did with each container, and the destination's containers this store doesn't have.</returns>
    /// <exception cref="StoreCopyException">
    /// A container failed, with <see cref="StoreCopyFailures.Stop"/>; it holds what the copy did before.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> is null, or <paramref name="options"/> has a value that isn't defined.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">This is the default implementation.</exception>
    public Task<StoreCopyResult> CopyStoreToAsync(IResourceServiceClient destination, StoreCopyOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't copy stores.");

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
    /// Purges every version whose <see cref="ResourceInfo.PurgeAt"/> has passed from the bin of each container that isn't
    /// read-only, telling the <see cref="IResourceCleanupHandler"/> about each one first. A read-only container keeps
    /// its bin until it's writable again. <see cref="ResourcefullOptionsBuilder.UseExpiry"/> calls it every interval,
    /// after <see cref="DeleteExpiredAsync"/>; call it yourself to purge another way.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="cancellationToken">Stops the purge; the versions already purged stay purged.</param>
    /// <returns>How many versions were purged; 0 when the provider has no bin.</returns>
    /// <exception cref="AggregateException">
    /// Some versions weren't purged, because the cleanup handler or the purge failed; the others were, and those stay
    /// for the next call. Its message counts them all, and it holds the first few failures.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The provider broke its <c>GetPurgeableAsync</c> contract: it listed the same versions twice, or listed one
    /// without a purge time.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">This is the default implementation.</exception>
    public Task<int> PurgeDeletedAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't keep deleted versions.");

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
}
