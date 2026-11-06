// The compiler doesn't expand <inheritdoc cref>, so it reports the parameters an overload inherits through it as
// undocumented.
#pragma warning disable CS1573

using System.Runtime.CompilerServices;

namespace Resourcefull;

/// <summary>
/// A client for working with the resources inside a single container. Obtain one from
/// <see cref="IResourceServiceClient.GetContainerClient(string)"/>.
/// </summary>
/// <remarks>
/// Every operation that contacts storage takes an optional <see cref="CancellationToken"/> as its last parameter. A
/// canceled operation throws <see cref="OperationCanceledException"/> and never returns a result, so a
/// caller that cancels can trust that nothing it did not see was reported as done.
/// </remarks>
public interface IResourceContainerClient
{
    /// <summary>
    /// The name of the container this client targets.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Reads the container's attributes as they're stored now: its name, its metadata, and the URI from the URI
    /// provider.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The container's attributes, or <see langword="null"/> if it doesn't exist.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ContainerInfo?> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a resource to the container.
    /// </summary>
    /// <param name="resourceName">The name of the uploaded resource.</param>
    /// <param name="content">
    /// The content to upload, read from its current position to its end. The stream is not disposed.
    /// </param>
    /// <param name="options">Optional configuration for the upload.</param>
    /// <param name="cancellationToken">Cancels the upload. A canceled upload stores nothing.</param>
    /// <returns>
    /// What was stored: the new version, its measured <see cref="ResourceInfo.ContentLength"/> and
    /// <see cref="ResourceInfo.ContentHash"/>, and <see cref="ResourceInfo.IsLatest"/> set.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace
    /// (<see cref="ArgumentNullException"/> when null); or <see cref="ResourceUploadOptions.Conditions"/> are set without
    /// <see cref="ResourceWriteMode.Replace"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="ResourceContentLengthMismatchException">
    /// <see cref="ResourceUploadOptions.Headers"/> declares a content length that differs from the content; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceConflictException">
    /// A resource with the same name already exists and <see cref="ResourceUploadOptions.Mode"/> is
    /// <see cref="ResourceWriteMode.Create"/>, the default.
    /// </exception>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <see cref="ResourceUploadOptions.Conditions"/>; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceContainerNotFoundException">
    /// The container does not exist: it was never created, or it was deleted before the write.
    /// </exception>
    /// <exception cref="ResourceUploadInterruptedException">
    /// The upload's version was deleted before the upload completed, as by a container delete running at the same time;
    /// nothing was stored.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ResourceUploadOptions.Conditions"/> is set and the storage provider doesn't support conditional uploads,
    /// or <see cref="ResourceUploadOptions.ExpiresAt"/> is set and it doesn't support expiry; nothing was stored.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container's policy refuses the upload; nothing was stored.</exception>
    /// <exception cref="ResourceUploadLimitException">
    /// The content is larger than the provider can store in one object; nothing was stored.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a resource to the container from an in-memory payload.
    /// </summary>
    /// <param name="content">The content to upload.</param>
    /// <inheritdoc cref="UploadAsync(string, Stream, ResourceUploadOptions?, CancellationToken)" path="/*[not(self::param[@name='content'])]"/>
    public Task<ResourceInfo> UploadAsync(string resourceName, BinaryData content, ResourceUploadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a previous version of a resource by creating a new current version from it, as long as that version still
    /// exists. In a container that keeps one version, a replaced version exists only with expiry on, for the five minutes
    /// after it's replaced.
    /// </summary>
    /// <param name="resourceName">The name of the resource to restore.</param>
    /// <param name="versionId">The version ID to restore.</param>
    /// <param name="cancellationToken">Cancels the restore. A canceled restore creates no version.</param>
    /// <returns>The <see cref="ResourceInfo"/> for the newly created version, or <see langword="null"/> if the resource or version does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null); or <paramref name="versionId"/> is null or empty.</exception>
    /// <exception cref="ResourcePolicyViolationException">The container's policy refuses the restored version; nothing was stored.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ResourceInfo?> RestoreVersionAsync(string resourceName, string versionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a resource, or part of one, as a stream. Prefer this for large resources to avoid buffering the whole
    /// payload in memory. Pass a range to resume a download or to seek in a video: only the range's bytes are read
    /// from storage, where the provider supports it.
    /// </summary>
    /// <param name="resourceName">The name of the resource to download.</param>
    /// <param name="versionId">
    /// The version to download; the current version when null. To download a resource in parts, pass the version ID of
    /// the first part's result to the rest, so they can't come from a version uploaded in between.
    /// </param>
    /// <param name="range">
    /// The bytes to download, or <see langword="null"/> for all of them. A range that runs past the end of the content
    /// is cut at the end.
    /// </param>
    /// <param name="cancellationToken">Cancels locating the resource. Pass a token to the returned stream's read methods to cancel the download itself.</param>
    /// <returns>
    /// The download result, or <see langword="null"/> if the resource does not exist. Its stream holds the whole
    /// content, or the range when one is given, as its <see cref="ResourceDownloadResult.Range"/> says, and
    /// <see cref="ResourceDownloadResult.TotalLength"/> is the whole content's length.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="range"/> has a negative offset or length.
    /// </exception>
    /// <exception cref="ResourceRangeNotSatisfiableException">
    /// <paramref name="range"/> starts past the end of the content.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId = null,
        ResourceRange? range = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a resource's content into memory.
    /// </summary>
    /// <param name="resourceName">The name of the resource to download.</param>
    /// <param name="versionId">The version to download; the current version when null.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The resource content, or <see langword="null"/> if the resource does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the attributes of a resource without downloading its content.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="versionId">The version to inspect; the current version when null.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The <see cref="ResourceInfo"/>, or <see langword="null"/> if the resource does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the current version of every resource in the container, or of those whose names start with
    /// <see cref="ResourceListOptions.Prefix"/>, ordered by name, fetching a page at a time as you enumerate. A name
    /// that exists for the whole listing appears exactly once; one created or deleted during it may or may not.
    /// </summary>
    /// <param name="options">The prefix and page size; <see langword="null"/> lists everything, 1,000 at a time.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The resources, ordered by name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceListOptions.PageSize"/> is outside 1 to <see cref="ResourceListOptions.MaxPageSize"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <remarks>
    /// The default implementation fetches the pages through <see cref="GetResourcePageAsync"/>.
    /// </remarks>
    public async IAsyncEnumerable<ResourceInfo> ListResourcesAsync(ResourceListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            ResourcePage page = await GetResourcePageAsync(options, continuationToken, cancellationToken).ConfigureAwait(false);
            foreach (ResourceInfo resource in page.Resources) yield return resource;
            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);
    }

    /// <summary>
    /// Gets one page of the current resources, ordered by name, for an API that hands the continuation token to its
    /// own callers. Each page is a query of its own, so there's no snapshot: a name that exists for the whole listing
    /// appears exactly once, and one created or deleted during it may or may not.
    /// </summary>
    /// <param name="options">The prefix and page size; <see langword="null"/> lists everything, 1,000 at a time.</param>
    /// <param name="continuationToken">
    /// The previous page's <see cref="ResourcePage.ContinuationToken"/>, or <see langword="null"/> for the first page.
    /// It must come from a listing with the same prefix and metadata match; the page size can change.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page, and the token for the next one if there is one.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="continuationToken"/> is malformed or comes from a listing of another prefix or metadata match, or
    /// <see cref="ResourceListOptions.MetadataMatch"/> has a key that can't be searched or a value too long to search for.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceListOptions.PageSize"/> is outside 1 to <see cref="ResourceListOptions.MaxPageSize"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ResourcePage> GetResourcePageAsync(ResourceListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every stored version of a resource: the current version first, with
    /// <see cref="ResourceInfo.IsLatest"/> set to <see langword="true"/>, then the rest from the most recently stored to
    /// the oldest.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>A (possibly empty) list of versions.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a resource and all its versions, or one version of it, if it exists. A storage provider may delete a
    /// resource with many versions in several steps, oldest first and the current version last: if the delete is
    /// canceled or fails partway, every version it didn't reach is still whole, the resource still has its current
    /// version, and calling it again finishes the job. A version uploaded while it runs may remain. In a container
    /// with a <see cref="ContainerPolicy.KeepDeletedFor"/>, the versions go to the container's bin instead,
    /// where <see cref="UndeleteAsync"/> can bring them back.
    /// </summary>
    /// <param name="resourceName">The name of the resource to delete.</param>
    /// <param name="versionId">
    /// The one version to delete; if it is the current version, the most recently stored remaining version becomes
    /// current. When null, the resource and every version of it are deleted.
    /// </param>
    /// <param name="conditions">
    /// What the name's current version must be like, decided by the delete itself; <see langword="null"/> for none. The
    /// conditions apply to the current version even when <paramref name="versionId"/> names another one. A name that
    /// doesn't exist meets no condition, so with conditions this throws rather than returning <see langword="false"/>
    /// for it.
    /// </param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> if anything was deleted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <paramref name="conditions"/>, or the name doesn't exist; nothing was deleted.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only; nothing was deleted.</exception>
    /// <exception cref="ResourceRetainedException">
    /// A version it would delete is retained or under a legal hold. In a container that keeps one version, it's also
    /// thrown when a version the current one replaced is protected, since deleting the current version would make that
    /// one current again. Nothing was deleted, unless the version was protected while a batched delete of a name with
    /// many large versions was under way.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The container has a <see cref="ContainerPolicy.KeepDeletedFor"/>, and the provider has no bin.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The container has a <see cref="ContainerPolicy.KeepDeletedFor"/>, and the store doesn't use
    /// <see cref="ResourcefullOptionsBuilder.UseExpiry"/>, whose sweep purges the bin.
    /// </exception>
    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a resource (or a specific version) exists in the container.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="versionId">The version to check; the current version when null.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns><see langword="true"/> if it exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<bool> ExistsAsync(string resourceName, string? versionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies a version of a resource, the current one by default, to a name in this or another container, as a new
    /// version there, without reading it through the application where the provider supports that (SQL Server does).
    /// The copy has a new version ID and <see cref="ResourceInfo.LastModified"/>, the source's content, hash, type and
    /// metadata, and <see cref="ResourceInfo.CopiedFrom"/> naming the version copied. It's stored and pruned like an
    /// upload, and checked against the destination container's policy.
    /// </summary>
    /// <param name="resourceName">The resource to copy.</param>
    /// <param name="destinationContainerName">The container to copy it to: this container's name to copy within it.</param>
    /// <param name="destinationName">The name to copy it to.</param>
    /// <param name="options">Which version to copy, and how to treat an existing destination; the defaults when null.</param>
    /// <param name="cancellationToken">Cancels the copy; a canceled copy stores nothing.</param>
    /// <returns>The new version, or <see langword="null"/> if the resource or version doesn't exist.</returns>
    /// <exception cref="ResourceConflictException">
    /// The destination name exists and <see cref="ResourceCopyOptions.Mode"/> is <see cref="ResourceWriteMode.Create"/>,
    /// the default.
    /// </exception>
    /// <exception cref="ResourceContainerNotFoundException">The destination container doesn't exist.</exception>
    /// <exception cref="ResourcePreconditionFailedException">The destination's current version doesn't meet <see cref="ResourceCopyOptions.DestinationConditions"/>.</exception>
    /// <exception cref="ResourcePolicyViolationException">The destination container's policy refuses the copy.</exception>
    /// <exception cref="ArgumentException">
    /// A name breaks the name rules (<see cref="ResourceNameException"/>), or is null (<see cref="ArgumentNullException"/>);
    /// or <see cref="ResourceCopyOptions.DestinationConditions"/> are set without <see cref="ResourceWriteMode.Replace"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ResourceCopyOptions.ExpiresAt"/> is set and the provider doesn't support expiry; nothing was stored.
    /// </exception>
    public Task<ResourceInfo?> CopyResourceAsync(string resourceName, string destinationContainerName, string destinationName,
        ResourceCopyOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a resource, every version of it, to a name in this or another container, all at once; renaming is a move
    /// within the container. Each version keeps its ID, <see cref="ResourceInfo.LastModified"/>, content and
    /// attributes, and the old name is gone afterwards. It's checked against both containers' policies.
    /// </summary>
    /// <param name="resourceName">The resource to move.</param>
    /// <param name="destinationContainerName">The container to move it to: this container's name to rename it.</param>
    /// <param name="destinationName">The name to move it to, which must not exist.</param>
    /// <param name="options">What the resource's current version must be like; none when null.</param>
    /// <param name="cancellationToken">Cancels the move; a canceled move changes nothing.</param>
    /// <returns>The moved resource's current version, or <see langword="null"/> if the resource doesn't exist.</returns>
    /// <exception cref="ResourceConflictException">The destination name exists.</exception>
    /// <exception cref="ResourceContainerNotFoundException">The destination container doesn't exist.</exception>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The current version doesn't meet <see cref="ResourceMoveOptions.SourceConditions"/>; or, with container policies,
    /// uploads to the name kept making new versions current while the move checked them, five times in a row.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">Either container's policy refuses the move.</exception>
    /// <exception cref="ArgumentException">
    /// A name breaks the name rules (<see cref="ResourceNameException"/>) or is null (<see cref="ArgumentNullException"/>),
    /// or the destination is where the resource already is.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't move resources.
    /// </exception>
    public Task<ResourceInfo?> MoveResourceAsync(string resourceName, string destinationContainerName, string destinationName,
        ResourceMoveOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a resource, every version of it, within this container: a
    /// <see cref="MoveResourceAsync(string, string, string, ResourceMoveOptions?, CancellationToken)"/> to
    /// <paramref name="newName"/> in this container, with the same results and exceptions.
    /// </summary>
    /// <param name="resourceName">The resource to rename.</param>
    /// <param name="newName">Its new name, which must not exist.</param>
    /// <param name="options">What the resource's current version must be like; none when null.</param>
    /// <param name="cancellationToken">Cancels the rename; a canceled rename changes nothing.</param>
    /// <returns>
    /// The renamed resource's current version, or <see langword="null"/> if the resource doesn't exist.
    /// </returns>
    public Task<ResourceInfo?> RenameResourceAsync(string resourceName, string newName,
        ResourceMoveOptions? options = null, CancellationToken cancellationToken = default)
        => MoveResourceAsync(resourceName, Name, newName, options, cancellationToken);

    /// <summary>
    /// Sets when a version expires, or with <see langword="null"/> makes it permanent, such as to extend a share. Only
    /// the expiry changes: the version's content, <see cref="ResourceInfo.LastModified"/> and ETag stay as they are.
    /// </summary>
    /// <param name="resourceName">The resource.</param>
    /// <param name="versionId">The version, or <see langword="null"/> for the current one.</param>
    /// <param name="expiresAt">When it expires; <see langword="null"/> never. Any time works, a past one included.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists, <see langword="false"/> if not, as when expiry deleted it first.</returns>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support expiry.
    /// </exception>
    public Task<bool> SetExpiryAsync(string resourceName, string? versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a version's metadata, without uploading it again. Only the metadata changes: the version keeps its ID,
    /// content, content type, <see cref="ResourceInfo.LastModified"/> and ETag, and no new version is made.
    /// </summary>
    /// <remarks>
    /// The whole dictionary is replaced. To add or change one entry, read the version's metadata, change it and set it:
    /// two callers doing that for the same version at once can each overwrite the other's change, and
    /// <see cref="ResourceConditions"/> can't prevent it, since the version's ID and content hash don't change.
    /// </remarks>
    /// <param name="resourceName">The resource.</param>
    /// <param name="versionId">The version; <see langword="null"/> for the current one.</param>
    /// <param name="metadata">The version's new metadata, which is copied.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't change a version's metadata.
    /// </exception>
    public Task<bool> SetResourceMetadataAsync(string resourceName, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes the content type a version is stored and served with, without uploading it again, such as to correct one
    /// declared wrongly. Only the type changes: the version keeps its ID, content, metadata,
    /// <see cref="ResourceInfo.LastModified"/> and ETag, and no new version is made. The type is checked against the
    /// container's <see cref="ContainerPolicy.AllowedContentTypes"/>, as an upload's is, but an
    /// <see cref="IResourceUploadInspector"/> doesn't see the change.
    /// </summary>
    /// <remarks>
    /// Browsers and caches that already hold the version keep the old type: its ETag and
    /// <see cref="ResourceInfo.LastModified"/> don't change, so a request that revalidates their copy is told it's
    /// still current.
    /// </remarks>
    /// <param name="resourceName">The resource.</param>
    /// <param name="versionId">The version; <see langword="null"/> for the current one.</param>
    /// <param name="contentType">The new type, such as <c>application/pdf</c>; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only, or doesn't allow the type.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't change a version's content type.
    /// </exception>
    public Task<bool> SetContentTypeAsync(string resourceName, string? versionId, string? contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps a version from being deleted until <paramref name="retainUntil"/>, by anyone or anything: a delete of it,
    /// of its name or of its container is refused with <see cref="ResourceRetainedException"/>, expiry leaves it until
    /// the date has passed, and pruning keeps it past the container's
    /// <see cref="ContainerPolicy.MaxVersions"/> until then. Use it for records that must be kept for a
    /// period, such as invoices. The date can only move later, never earlier, and can't be cleared, so a retained
    /// version is safe from a mistaken or malicious change too. Who may set it is up to the application.
    /// </summary>
    /// <remarks>
    /// The version's content and hash never change; its metadata and content type still can. A move takes the date
    /// with the version.
    /// </remarks>
    /// <param name="resourceName">The resource.</param>
    /// <param name="versionId">The version; <see langword="null"/> for the current one.</param>
    /// <param name="retainUntil">Until when the version can't be deleted.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="retainUntil"/> is before the version's <see cref="ResourceInfo.RetainUntil"/>; nothing changed.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support retention.
    /// </exception>
    public Task<bool> SetRetentionAsync(string resourceName, string? versionId, DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or lifts a legal hold on a version. While it's held, it can't be deleted, by anyone or anything, whatever
    /// its <see cref="ResourceInfo.RetainUntil"/>: a delete of it, of its name or of its container is refused with
    /// <see cref="ResourceRetainedException"/>, and expiry and pruning leave it. Use it to keep what a lawsuit or an
    /// investigation needs. Who may set or lift it is up to the application.
    /// </summary>
    /// <param name="resourceName">The resource.</param>
    /// <param name="versionId">The version; <see langword="null"/> for the current one.</param>
    /// <param name="hold">Whether the version is held.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the version exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support retention.
    /// </exception>
    public Task<bool> SetLegalHoldAsync(string resourceName, string? versionId, bool hold,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the versions in the container's bin, newest deletion first, fetching a page at a time as you enumerate.
    /// In a container with a <see cref="ContainerPolicy.KeepDeletedFor"/>, a deleted or pruned version goes to
    /// the bin, where <see cref="UndeleteAsync"/> can bring it back until its <see cref="ResourceInfo.PurgeAt"/>.
    /// </summary>
    /// <param name="options">
    /// The prefix and page size; <see langword="null"/> lists everything, 1,000 at a time. A metadata match isn't
    /// supported.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The deleted versions, each with <see cref="ResourceInfo.DeletedAt"/> and <see cref="ResourceInfo.PurgeAt"/> set.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceListOptions.PageSize"/> is outside 1 to <see cref="ResourceListOptions.MaxPageSize"/>, or
    /// <see cref="ResourceListOptions.MetadataMatch"/> is set.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider has no bin.
    /// </exception>
    /// <remarks>
    /// The default implementation fetches the pages through <see cref="GetDeletedPageAsync"/>.
    /// </remarks>
    public async IAsyncEnumerable<ResourceInfo> ListDeletedAsync(ResourceListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            ResourcePage page = await GetDeletedPageAsync(options, continuationToken, cancellationToken).ConfigureAwait(false);
            foreach (ResourceInfo resource in page.Resources) yield return resource;
            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);
    }

    /// <summary>
    /// Gets one page of the versions in the container's bin, newest deletion first, for an API that hands the
    /// continuation token to its own callers. With a prefix, a page can hold fewer versions than
    /// <see cref="ResourceListOptions.PageSize"/>, or none, while more follow: only the continuation token says when the
    /// listing is done.
    /// </summary>
    /// <param name="options">
    /// The prefix and page size; <see langword="null"/> lists everything, 1,000 at a time. A metadata match isn't
    /// supported.
    /// </param>
    /// <param name="continuationToken">
    /// The previous page's <see cref="ResourcePage.ContinuationToken"/>, or <see langword="null"/> for the first page.
    /// It must come from a listing of the bin with the same prefix; the page size can change.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page, and the token for the next one if there is one.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="continuationToken"/> is malformed or comes from another listing, or
    /// <see cref="ResourceListOptions.MetadataMatch"/> is set.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ResourceListOptions.PageSize"/> is outside 1 to <see cref="ResourceListOptions.MaxPageSize"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider has no bin.
    /// </exception>
    public Task<ResourcePage> GetDeletedPageAsync(ResourceListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings a version back from the container's bin, or with no <paramref name="versionId"/> every version of the
    /// name there. Each comes back as an older version, so the name's current version stays current; if the name has
    /// none, the most recently stored version that came back becomes current. To make one of them current, restore it with
    /// <see cref="RestoreVersionAsync"/>, as for any older version.
    /// </summary>
    /// <param name="resourceName">The resource.</param>
    /// <param name="versionId">The version, or <see langword="null"/> for every version of the name in the bin.</param>
    /// <param name="cancellationToken">Cancels the undelete.</param>
    /// <returns><see langword="true"/> if anything came back; <see langword="false"/> if the bin has none of it.</returns>
    /// <exception cref="ResourcePolicyViolationException">
    /// The container is read-only, or what comes back wouldn't fit its <see cref="ContainerPolicy.MaxContainerSize"/>
    /// or <see cref="ContainerPolicy.MaxResourceCount"/>, which the bin doesn't count toward; nothing came back.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider has no bin.
    /// </exception>
    public Task<bool> UndeleteAsync(string resourceName, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a version in the container's bin for good, or with no <paramref name="versionId"/> every version of the
    /// name there, before its <see cref="ResourceInfo.PurgeAt"/>: for example, to erase personal data straight away.
    /// The <see cref="IResourceCleanupHandler"/> isn't told, as it isn't told about the application's own deletes.
    /// </summary>
    /// <param name="resourceName">The resource.</param>
    /// <param name="versionId">The version, or <see langword="null"/> for every version of the name in the bin.</param>
    /// <param name="cancellationToken">Cancels the purge.</param>
    /// <returns><see langword="true"/> if anything was purged; <see langword="false"/> if the bin has none of it.</returns>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only; nothing was purged.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider has no bin.
    /// </exception>
    public Task<bool> PurgeDeletedAsync(string resourceName, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets what the container holds: the size of every version, and how many resources and versions there are. Use it
    /// to show a user their storage, or against <see cref="ContainerPolicy.MaxContainerSize"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The container's usage; nothing stored when the container doesn't exist.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ContainerUsage> GetUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the policy this container's writes are checked against: the defaults from
    /// <see cref="ResourcefullOptionsBuilder.UseContainerPolicies"/>, with what the policy provider gives for this
    /// container in place of each limit it sets. Its <see cref="ContainerPolicy.MaxVersions"/> is always the limit
    /// in force: the policy's, then the defaults', then <see cref="ResourcefullOptionsBuilder.UseVersioning"/>'s, then 1.
    /// With <see cref="GetUsageAsync"/>, use it to show a user their limits,
    /// such as "4.2 GB of 5 GB". It's cached, and stands in for the provider while it fails, as for writes.
    /// </summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The container's policy; without container policies, one that sets only the version limit.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<ContainerPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the container's metadata, which otherwise is set only when the container is created. The whole
    /// dictionary is replaced, including the <c>resourcefull.import</c> entry that marks an unfinished import; to change
    /// one entry, read the metadata with <see cref="GetInfoAsync"/>, change it and set it. An import running at the same
    /// time replaces the metadata again when it finishes.
    /// </summary>
    /// <param name="metadata">The container's new metadata, which is copied.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns><see langword="true"/> if the container exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">The provider doesn't change container metadata.</exception>
    public Task<bool> SetMetadataAsync(IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prunes every resource in the container to its <see cref="ContainerPolicy.MaxVersions"/>,
    /// as each upload prunes its own name, telling the <see cref="IResourceCleanupHandler"/> as a prune does. Run it after
    /// an import, which keeps every version it was given, or after a prune failed and its name hasn't been written since.
    /// With <see cref="ResourcefullOptionsBuilder.UseExpiry"/>, the versions past the limit are set to expire five
    /// minutes later rather than deleted at once, as a prune does.
    /// </summary>
    /// <param name="cancellationToken">Stops the pruning; the versions already pruned stay pruned.</param>
    /// <returns>How many versions were past the limit. A container that doesn't exist has nothing to prune.</returns>
    /// <exception cref="AggregateException">
    /// Some resources weren't pruned, because the cleanup handler or the provider failed for them; the others were, and
    /// the failed ones keep their extra versions until a later prune. Its message counts them, and it holds the first few
    /// failures.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container is read-only.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<long> PruneAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Indexes the keys <see cref="ResourcefullOptionsBuilder.UseMetadataSearch"/> names for every current version in
    /// the container, so a listing by <see cref="ResourceListOptions.MetadataMatch"/> finds versions stored before a key was
    /// added. New writes are indexed as they're stored, so run it once after adding a key, for each container that
    /// holds versions from before. With a provider that keeps no index, it does nothing, since searches read every name.
    /// </summary>
    /// <param name="cancellationToken">Stops the indexing; what's indexed by then stays indexed, and running it again finishes.</param>
    /// <returns>How many versions were indexed. A container that doesn't exist has nothing to index.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<long> IndexMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the container's largest versions, or the ones that expire soonest, current and older versions alike, each
    /// with <see cref="ResourceInfo.IsLatest"/> set. Use it to see what's filling a container as it nears
    /// <see cref="ContainerPolicy.MaxContainerSize"/>, which counts every version, or what expiry deletes next.
    /// It's meant for an administrator looking now and then: the store may read all of the container's versions to
    /// answer it.
    /// </summary>
    /// <param name="order">Which versions come first; <see cref="ResourceVersionOrder"/> says how ties are ordered.</param>
    /// <param name="count">The most versions to return, from 1 to 1,000.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The versions, in <paramref name="order"/>; empty when there are none, or the container doesn't exist.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="order"/> isn't one of the values defined, or <paramref name="count"/> is outside 1 to 1,000.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public Task<IReadOnlyList<ResourceInfo>> GetTopVersionsAsync(ResourceVersionOrder order, int count = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts an upload session: a version of <paramref name="resourceName"/> whose content arrives over several
    /// appends, which can resume after a dropped connection, even from another process, and which becomes current only
    /// when <see cref="CompleteUploadAsync"/> is called. Nothing shows the version until then. A session nothing is
    /// appended to for 24 hours is deleted by the expiry sweep, so turn on
    /// <see cref="ResourcefullOptionsBuilder.UseExpiry"/>, or cancel a session you abandon.
    /// </summary>
    /// <param name="resourceName">The name the version will be stored under.</param>
    /// <param name="options">The version's content type, expected length, metadata and expiry.</param>
    /// <param name="cancellationToken">Cancels the start; a canceled start leaves no session.</param>
    /// <returns>The new session, with nothing stored.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the rules for a new name.</exception>
    /// <exception cref="ResourceContainerNotFoundException">The container doesn't exist.</exception>
    /// <exception cref="ResourcePolicyViolationException">
    /// The container's policy refuses the upload: it's read-only, doesn't allow the content type, or the expected
    /// length is over a limit.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support upload sessions, or <see cref="ResourceUploadSessionOptions.ExpiresAt"/> is set and it
    /// doesn't support expiry.
    /// </exception>
    public Task<ResourceUploadSession> StartUploadAsync(string resourceName, ResourceUploadSessionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an upload session as it stands, to find where to resume: its <see cref="ResourceUploadSession.Offset"/> is how
    /// many bytes are stored.
    /// </summary>
    /// <param name="resourceName">The name the session's version will be stored under.</param>
    /// <param name="uploadId">The session's ID.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The session, or <see langword="null"/> if there's none: it was completed, canceled or swept.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules, or <paramref name="uploadId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support upload sessions.
    /// </exception>
    public Task<ResourceUploadSession?> GetUploadAsync(string resourceName, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends <paramref name="content"/> to an upload session, reading it to its end. An append that fails partway may
    /// keep part of what it sent, depending on the provider: get the session to find where its content now ends, and
    /// resume from there.
    /// </summary>
    /// <param name="resourceName">The name the session's version will be stored under.</param>
    /// <param name="uploadId">The session's ID.</param>
    /// <param name="offset">Where the content starts: the session's <see cref="ResourceUploadSession.Offset"/>.</param>
    /// <param name="content">The content to append.</param>
    /// <param name="cancellationToken">Cancels the append; what's already stored stays stored.</param>
    /// <returns>The session after the append, or <see langword="null"/> if there's no such session.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules, or <paramref name="uploadId"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="ResourceUploadOffsetException">
    /// The stored content doesn't end at <paramref name="offset"/>; nothing was stored. Resume from its
    /// <see cref="ResourceUploadOffsetException.CurrentOffset"/>.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">
    /// The content would take the version past the container's <see cref="ContainerPolicy.MaxResourceSize"/>, or
    /// past what the whole container may hold. Part of what arrived before the limit may stay stored.
    /// </exception>
    /// <exception cref="ResourceUploadLimitException">
    /// The session already holds as much as its provider can store. Part of what arrived before the limit may stay
    /// stored.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support upload sessions.
    /// </exception>
    public Task<ResourceUploadSession?> AppendUploadAsync(string resourceName, string uploadId, long offset, Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes an upload session, making its version the name's current one, as an upload with the same mode and
    /// conditions would. It reads the stored content once, to measure and hash it and to pass it to the upload
    /// inspector, so it takes about as long as reading the content back. Older versions are pruned as after an upload.
    /// </summary>
    /// <param name="resourceName">The name the session's version will be stored under.</param>
    /// <param name="uploadId">The session's ID.</param>
    /// <param name="options">The write's mode and conditions; a create-only write when <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the completion; a canceled completion leaves the session as it was.</param>
    /// <returns>The version as stored, or <see langword="null"/> if there's no such session.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceName"/> breaks the name rules, <paramref name="uploadId"/> is empty, or
    /// <see cref="ResourceCompleteUploadOptions.Conditions"/> are given with <see cref="ResourceWriteMode.Create"/>.
    /// </exception>
    /// <exception cref="ResourceConflictException">
    /// <see cref="ResourceCompleteUploadOptions.Mode"/> is <see cref="ResourceWriteMode.Create"/> and the name has a current
    /// version. The session stays, so it can be completed with <see cref="ResourceWriteMode.Replace"/>.
    /// </exception>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <see cref="ResourceCompleteUploadOptions.Conditions"/>. The session stays.
    /// </exception>
    /// <exception cref="ResourceContentRefusedException">
    /// The upload inspector refused the content. The session is deleted.
    /// </exception>
    /// <exception cref="ResourceContentLengthMismatchException">
    /// The session declared a length at its start, and a different length was appended. The session stays.
    /// </exception>
    /// <exception cref="ResourceUploadOffsetException">
    /// An append stored more content while the completion read it. The session stays, and can be completed again.
    /// </exception>
    /// <exception cref="ResourcePolicyViolationException">The container's policy refuses the version. The session stays.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support upload sessions.
    /// </exception>
    public Task<ResourceInfo?> CompleteUploadAsync(string resourceName, string uploadId, ResourceCompleteUploadOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an upload session, deleting what it stored.
    /// </summary>
    /// <param name="resourceName">The name the session's version would have been stored under.</param>
    /// <param name="uploadId">The session's ID.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> if it was canceled; <see langword="false"/> if there's no such session.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> breaks the name rules, or <paramref name="uploadId"/> is empty.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="NotSupportedException">
    /// The provider doesn't support upload sessions.
    /// </exception>
    public Task<bool> CancelUploadAsync(string resourceName, string uploadId, CancellationToken cancellationToken = default);
}
