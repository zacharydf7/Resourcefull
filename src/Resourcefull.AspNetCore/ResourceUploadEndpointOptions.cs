using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Resourcefull.AspNetCore;

/// <summary>
/// How the resumable upload endpoints that <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploads"/>
/// maps decide where uploads go, and what they do once one is stored.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceUploadEndpointOptions
{
    /// <summary>
    /// Decides where an upload goes, when a client starts one: the container, the name and the mode, and the
    /// version's content type, metadata and expiry. Return <see langword="null"/> to refuse the upload, which answers
    /// 403 Forbidden. Required.
    /// </summary>
    /// <remarks>
    /// The request's <see cref="ResourceUploadRequest.Metadata"/> comes from the client, so treat it as a suggestion,
    /// as you would a form field: check it before using it, and don't let it choose a container or a name the user
    /// may not write to. The container's policies, the name rules and the upload inspector apply as to any upload.
    /// </remarks>
    public Func<HttpContext, ResourceUploadRequest, Task<ResourceUploadTarget?>>? ResolveAsync { get; set; }

    /// <summary>
    /// Called once an upload is stored and has become its name's current version, before the client is told it
    /// finished, typically to record the file in the application's own database. If it throws, the version stays
    /// stored, the exception is logged, and the client gets 500 Internal Server Error; a client that resumes the upload
    /// later is told it finished. So save in a way that tolerates a missed call, and since two requests that complete
    /// the same upload at once each call it, make it safe to call twice for the same version.
    /// </summary>
    public Func<HttpContext, ResourceInfo, Task>? OnCompletedAsync { get; set; }

    /// <summary>
    /// The store uploads go to, added with <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/>;
    /// <see langword="null"/>, the default, for the store <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/>
    /// adds. Each store's upload URLs work only with its own endpoints.
    /// </summary>
    public string? StoreName { get; set; }

    /// <summary>
    /// The largest upload the endpoints take, in bytes, sent to clients in <c>Tus-Max-Size</c>; a larger one is refused
    /// with 413 Content Too Large before any of it is sent. <see langword="null"/>, the default, sets no limit of its
    /// own, and leaves sizes to the container's policies, which are also checked before anything is sent. Either way,
    /// no request stores more than its upload's length.
    /// </summary>
    public long? MaxSize { get; set; }
}

/// <summary>
/// How the resumable upload endpoints that <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploadLinks"/>
/// maps for signed upload links find their store, how large an upload they take, and what they do once an upload is
/// stored. Each link decides where its uploads go.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceUploadLinkEndpointOptions
{
    /// <summary>
    /// Called once an upload is stored and has become its name's current version, before the client is told it
    /// finished, as <see cref="ResourceUploadEndpointOptions.OnCompletedAsync"/> is, and with the same caveats: it may
    /// be missed when it throws, and called twice for one version.
    /// </summary>
    public Func<HttpContext, ResourceInfo, Task>? OnCompletedAsync { get; set; }

    /// <summary>
    /// The store whose upload links the endpoints take, added with
    /// <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/> and given signed links with
    /// <see cref="ResourceLinkServiceCollectionExtensions.AddResourcefullLinks"/>; <see langword="null"/>, the default,
    /// for the store <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> adds.
    /// </summary>
    public string? StoreName { get; set; }

    /// <summary>
    /// The largest upload the endpoints take through any link, in bytes, sent to clients in <c>Tus-Max-Size</c>; a
    /// larger one is refused with 413 Content Too Large before any of it is sent. <see langword="null"/>, the default,
    /// leaves sizes to each link's <see cref="ResourceUploadLink.MaxSize"/> and the container's policies. The endpoints
    /// lift ASP.NET Core's limit on the size of a request's body, so when neither the link nor the container sets a
    /// limit, an upload has no limit at all.
    /// </summary>
    public long? MaxSize { get; set; }
}

/// <summary>
/// What a client asks to upload, given to <see cref="ResourceUploadEndpointOptions.ResolveAsync"/>.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Length">The upload's length in bytes, from the request's <c>Upload-Length</c>.</param>
/// <param name="Metadata">
/// The request's <c>Upload-Metadata</c>, decoded: what the client says about the file, such as <c>filename</c> and
/// <c>filetype</c>, which tus clients usually send. It comes from the client, so check it before using it.
/// </param>
public sealed record ResourceUploadRequest(long Length, IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Where an upload goes, and what its version is stored with, returned by
/// <see cref="ResourceUploadEndpointOptions.ResolveAsync"/>.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The container to store the upload in.</param>
/// <param name="ResourceName">The name to store it under.</param>
public sealed record ResourceUploadTarget(string ContainerName, string ResourceName)
{
    /// <summary>
    /// What to do when the name already has a current version: <see cref="ResourceWriteMode.Create"/>, the default,
    /// refuses the upload with 409 Conflict when it starts, before any of it is sent, and
    /// <see cref="ResourceWriteMode.Replace"/> replaces the version when the upload completes.
    /// </summary>
    public ResourceWriteMode Mode { get; init; } = ResourceWriteMode.Create;

    /// <summary>The version's content type; <see langword="null"/> stores it without one.</summary>
    public string? ContentType { get; init; }

    /// <summary>The metadata to store with the version.</summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>When the version expires; <see langword="null"/> for never. See <see cref="ResourceUploadSessionOptions.ExpiresAt"/>.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// The version's retention date, before which it can't be deleted; <see langword="null"/> for none. See
    /// <see cref="ResourceUploadSessionOptions.RetainUntil"/>.
    /// </summary>
    public DateTimeOffset? RetainUntil { get; init; }

    /// <summary>Whether the version is placed under a legal hold. See <see cref="ResourceUploadSessionOptions.LegalHold"/>.</summary>
    public bool LegalHold { get; init; }
}

/// <summary>
/// Adds what the resumable upload endpoints need to a CORS policy, for a page on another origin.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullUploadsCorsPolicyBuilderExtensions
{
    /// <summary>
    /// Lets a page on another origin use the endpoints <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploads"/>
    /// maps: adds their methods and request headers to <paramref name="policy"/>, and exposes the response headers tus
    /// clients read, such as <c>Location</c> and <c>Upload-Offset</c>. It adds to what the policy already allows. Choose
    /// the origins yourself, with <c>WithOrigins</c>.
    /// </summary>
    /// <param name="policy">The CORS policy the endpoints use.</param>
    /// <returns><paramref name="policy"/>, for chaining.</returns>
    public static CorsPolicyBuilder WithResourcefullUploadHeaders(this CorsPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy
            .WithMethods("POST", "HEAD", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Tus-Resumable", "Upload-Length", "Upload-Metadata", "Upload-Offset", "Content-Type", "X-HTTP-Method-Override")
            .WithExposedHeaders("Location", "Tus-Resumable", "Tus-Version", "Tus-Extension", "Tus-Max-Size", "Upload-Offset", "Upload-Length",
                "Upload-Expires");
    }
}
