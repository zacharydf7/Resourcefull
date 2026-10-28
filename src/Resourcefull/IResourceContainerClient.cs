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
    /// The attributes of the container this client targets.
    /// </summary>
    public ResourceContainerInfo ResourceContainerInfo { get; }

    /// <summary>
    /// Uploads a resource to the container.
    /// </summary>
    /// <param name="resourceName">The name of the uploaded resource.</param>
    /// <param name="content">The content to upload.</param>
    /// <param name="uploadOptions">Optional configuration for the upload.</param>
    /// <returns>The <see cref="ResourceInfo"/> for the uploaded resource.</returns>
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
    /// <exception cref="ResourceConflictException">
    /// Versioning is disabled, a resource with the same name already exists and
    /// <see cref="ResourceUploadOptions.Overwrite"/> is <see langword="false"/>.
    /// </exception>
    /// <exception cref="ResourceContainerNotFoundException">
    /// The container does not exist: it was never created, or it was deleted before the write.
    /// </exception>
    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions = null);

    /// <inheritdoc cref="UploadAsync(string, Stream, ResourceUploadOptions?)"/>
    /// <param name="cancellationToken">Cancels the upload. A cancelled upload stores nothing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<ResourceInfo> UploadAsync(string resourceName, Stream content, ResourceUploadOptions? uploadOptions, CancellationToken cancellationToken);

    /// <summary>
    /// Uploads a resource to the container from an in-memory payload.
    /// </summary>
    /// <inheritdoc cref="UploadAsync(string, Stream, ResourceUploadOptions?)"/>
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
    /// <exception cref="ArgumentException"><paramref name="resourceName"/> or <paramref name="versionId"/> is null or empty.</exception>
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
    /// Lists every stored version of a resource, most recent first. The current version has
    /// <see cref="ResourceInfo.IsLatest"/> set to <see langword="true"/>.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <returns>A (possibly empty) list of versions.</returns>
    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName);

    /// <inheritdoc cref="GetResourceVersionsAsync(string)"/>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(string resourceName, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a resource (or a specific version) if it exists.
    /// </summary>
    /// <param name="resourceName">The name of the resource to delete.</param>
    /// <param name="versionId">The version to delete; the current version when null.</param>
    /// <returns><see langword="true"/> if a resource was deleted; otherwise <see langword="false"/>.</returns>
    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId = null);

    /// <inheritdoc cref="DeleteResourceIfExistsAsync(string, string?)"/>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<bool> DeleteResourceIfExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a resource (or a specific version) exists in the container.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="versionId">The version to check; the current version when null.</param>
    /// <returns><see langword="true"/> if it exists; otherwise <see langword="false"/>.</returns>
    public Task<bool> ExistsAsync(string resourceName, string? versionId = null);

    /// <inheritdoc cref="ExistsAsync(string, string?)"/>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<bool> ExistsAsync(string resourceName, string? versionId, CancellationToken cancellationToken);
}
