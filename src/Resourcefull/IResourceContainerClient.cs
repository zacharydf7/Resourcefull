// The compiler doesn't expand <inheritdoc cref>, so it reports the parameters an overload inherits through it as
// undocumented.
#pragma warning disable CS1573

using System.Runtime.CompilerServices;

namespace Resourcefull;

/// <summary>
/// A client for working with the resources inside a single container. Obtain one from
/// <see cref="IResourceServiceClient.GetResourceContainerClient(string)"/>.
/// </summary>
/// <remarks>
/// Every operation has an overload that takes a <see cref="CancellationToken"/> as its last parameter. A
/// cancelled operation throws <see cref="OperationCanceledException"/> and never returns a result, so a
/// caller that cancels can trust that nothing it did not see was reported as done.
/// </remarks>
public interface IResourceContainerClient : IDisposable
{
    /// <summary>
    /// The attributes of the container this client targets, as known when the client was made: the stored ones for a
    /// client from <see cref="IResourceServiceClient.CreateResourceContainerAsync(string, IDictionary{string, string}?)"/>
    /// or <see cref="IResourceServiceClient.CreateResourceContainerIfNotExistsAsync(string, IDictionary{string, string}?)"/>,
    /// the name and URI but no metadata for one from <see cref="IResourceServiceClient.GetResourceContainerClient(string)"/>.
    /// </summary>
    public ResourceContainerInfo ResourceContainerInfo { get; }

    /// <summary>
    /// Uploads a resource to the container.
    /// </summary>
    /// <param name="resourceName">The name of the uploaded resource.</param>
    /// <param name="content">
    /// The content to upload, read from its current position to its end. The stream is not disposed.
    /// </param>
    /// <param name="uploadOptions">Optional configuration for the upload.</param>
    /// <returns>
    /// What was stored: the new version, its measured <see cref="ResourceInfo.ContentLength"/> and
    /// <see cref="ResourceInfo.ContentHash"/>, and <see cref="ResourceInfo.IsLatest"/> set.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace
    /// (<see cref="ArgumentNullException"/> when null); or <see cref="ResourceUploadOptions.CreateOnly"/> and
    /// <see cref="ResourceUploadOptions.Conditions"/> are both set.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="ResourceContentLengthMismatchException">
    /// <see cref="ResourceUploadOptions.Headers"/> declares a content length that differs from the content; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceConflictException">
    /// A resource with the same name already exists and the upload is create-only: either
    /// <see cref="ResourceUploadOptions.CreateOnly"/> is set, or versioning is disabled and
    /// <see cref="ResourceUploadOptions.Overwrite"/> is <see langword="false"/>.
    /// </exception>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <see cref="ResourceUploadOptions.Conditions"/>; nothing was stored.
    /// </exception>
    /// <exception cref="ResourceContainerNotFoundException">
    /// The container does not exist: it was never created, or it was deleted before the write.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="ResourceUploadOptions.Conditions"/> is set and the storage provider doesn't support conditional uploads.
    /// </exception>
    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions = null);

    /// <inheritdoc cref="UploadAsync(string, Stream, ResourceUploadOptions?)"/>
    /// <param name="cancellationToken">Cancels the upload. A cancelled upload stores nothing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a resource to the container from an in-memory payload.
    /// </summary>
    /// <param name="content">The content to upload.</param>
    /// <inheritdoc cref="UploadAsync(string, Stream, ResourceUploadOptions?)" path="/*[not(self::param[@name='content'])]"/>
    public Task<ResourceInfo> UploadAsync(string resourceName, BinaryData content, ResourceUploadOptions? uploadOptions = null);

    /// <inheritdoc cref="UploadAsync(string, BinaryData, ResourceUploadOptions?)"/>
    /// <param name="cancellationToken">Cancels the upload. A cancelled upload stores nothing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceInfo> UploadAsync(string resourceName, BinaryData content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken);

    /// <summary>
    /// Restores a previous version of a resource by creating a new current version from it.
    /// </summary>
    /// <param name="resourceName">The name of the resource to restore.</param>
    /// <param name="versionId">The version ID to restore.</param>
    /// <returns>The <see cref="ResourceInfo"/> for the newly created version, or <see langword="null"/> if the resource or version does not exist.</returns>
    /// <exception cref="InvalidOperationException">Versioning is not enabled.</exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null); or <paramref name="versionId"/> is null or empty.</exception>
    public Task<ResourceInfo?> RestoreVersionAsync(string resourceName, string versionId);

    /// <inheritdoc cref="RestoreVersionAsync(string, string)"/>
    /// <param name="cancellationToken">Cancels the restore. A cancelled restore creates no version.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceInfo?> RestoreVersionAsync(string resourceName, string versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a resource as a stream. Prefer this for large resources to avoid buffering the whole
    /// payload in memory.
    /// </summary>
    /// <param name="resourceName">The name of the resource to download.</param>
    /// <param name="versionId">The version to download; the current version when null.</param>
    /// <returns>The download result, or <see langword="null"/> if the resource does not exist. When non-null, the stream is guaranteed to be present.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId = null);

    /// <inheritdoc cref="DownloadStreamingAsync(string, string?)"/>
    /// <param name="cancellationToken">Cancels locating the resource. Pass a token to the returned stream's read methods to cancel the download itself.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceDownloadResult?> DownloadStreamingAsync(string resourceName, string? versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a resource's content into memory.
    /// </summary>
    /// <param name="resourceName">The name of the resource to download.</param>
    /// <param name="versionId">The version to download; the current version when null.</param>
    /// <returns>The resource content, or <see langword="null"/> if the resource does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId = null);

    /// <inheritdoc cref="DownloadContentAsync(string, string?)"/>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceContent?> DownloadContentAsync(string resourceName, string? versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves the attributes of a resource without downloading its content.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="versionId">The version to inspect; the current version when null.</param>
    /// <returns>The <see cref="ResourceInfo"/>, or <see langword="null"/> if the resource does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId = null);

    /// <inheritdoc cref="GetResourceInfoAsync(string, string?)"/>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceInfo?> GetResourceInfoAsync(string resourceName, string? versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the current version of every resource in the container.
    /// </summary>
    /// <returns>A (possibly empty) list of resource attributes.</returns>
    public Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync();

    /// <inheritdoc cref="GetResourcesAsync()"/>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<IReadOnlyList<ResourceInfo>> GetResourcesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lists the current version of every resource in the container, or of those whose names start with
    /// <see cref="ResourceListOptions.Prefix"/>, ordered by name, fetching a page at a time as you enumerate. A name
    /// that exists for the whole listing appears exactly once; one created or deleted during it may or may not.
    /// </summary>
    /// <param name="options">The prefix and page size; <see langword="null"/> lists everything, 1,000 at a time.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The resources, ordered by name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceListOptions.PageSize"/> is outside 1 to <see cref="ResourceListOptions.MaxPageSize"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <remarks>
    /// The default implementation, kept so implementations written for 3.0 still compile, fetches the pages through
    /// <see cref="GetResourcePageAsync"/>.
    /// </remarks>
    public async IAsyncEnumerable<ResourceInfo> ListResourcesAsync(ResourceListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            ResourcePage page = await GetResourcePageAsync(options, continuationToken, cancellationToken);
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
    /// It must come from a listing with the same prefix; the page size can change.
    /// </param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The page, and the token for the next one if there is one.</returns>
    /// <exception cref="ArgumentException"><paramref name="continuationToken"/> is malformed or comes from a listing of another prefix.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceListOptions.PageSize"/> is outside 1 to <see cref="ResourceListOptions.MaxPageSize"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="NotSupportedException">
    /// This default implementation, kept so implementations written for 3.0 still compile, doesn't list by page.
    /// </exception>
    public Task<ResourcePage> GetResourcePageAsync(ResourceListOptions? options = null, string? continuationToken = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} doesn't list resources by page.");

    /// <summary>
    /// Lists every stored version of a resource: the current version first, with
    /// <see cref="ResourceInfo.IsLatest"/> set to <see langword="true"/>, then the rest from the most recently stored to
    /// the oldest.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <returns>A (possibly empty) list of versions.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName);

    /// <inheritdoc cref="GetResourceVersionsAsync(string)"/>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a resource and all its versions, or one version of it, if it exists.
    /// </summary>
    /// <param name="resourceName">The name of the resource to delete.</param>
    /// <param name="versionId">
    /// The one version to delete; if it is the current version, the most recently stored remaining version becomes
    /// current. When null, the resource and every version of it are deleted.
    /// </param>
    /// <returns><see langword="true"/> if anything was deleted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null);

    /// <inheritdoc cref="DeleteResourceIfExistsAsync(string, string?)"/>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes as <see cref="DeleteResourceIfExistsAsync(string, string?, CancellationToken)"/> does, only if the
    /// name's current version meets <paramref name="conditions"/>, decided by the delete itself. The conditions apply to
    /// the current version even when <paramref name="versionId"/> names another one. A name that doesn't exist meets no
    /// condition, so with conditions this throws rather than returning <see langword="false"/> for it.
    /// </summary>
    /// <param name="resourceName">The name of the resource to delete.</param>
    /// <param name="versionId">The one version to delete, or <see langword="null"/> for the resource and every version.</param>
    /// <param name="conditions">What the name's current version must be like; <see langword="null"/> for none.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> if anything was deleted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The name's current version doesn't meet <paramref name="conditions"/>, or the name doesn't exist; nothing was deleted.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="NotSupportedException">
    /// The implementation doesn't support conditions: this default implementation, kept so implementations written for
    /// 3.0 still compile, supports only <see langword="null"/> or empty <paramref name="conditions"/>.
    /// </exception>
    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, ResourceConditions? conditions,
        CancellationToken cancellationToken)
        => conditions is null or { IsEmpty: true }
            ? DeleteResourceIfExistsAsync(resourceName, versionId, cancellationToken)
            : throw new NotSupportedException($"{GetType().Name} doesn't support conditional deletes.");

    /// <summary>
    /// Checks whether a resource (or a specific version) exists in the container.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="versionId">The version to check; the current version when null.</param>
    /// <returns><see langword="true"/> if it exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null, empty, longer than 256 characters, or starts or ends with whitespace (<see cref="ArgumentNullException"/> when null).</exception>
    public Task<bool> ExistsAsync(string resourceName, string? versionId = null);

    /// <inheritdoc cref="ExistsAsync(string, string?)"/>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<bool> ExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken);
}
