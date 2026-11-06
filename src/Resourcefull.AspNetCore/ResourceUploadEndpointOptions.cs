using System.Collections.ObjectModel;
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
    /// 403 Forbidden.
    /// </summary>
    /// <remarks>
    /// The request's <see cref="ResourceUploadRequest.Metadata"/> comes from the client, so treat it as a suggestion,
    /// as you would a form field: check it before using it, and don't let it choose a container or a name the user
    /// may not write to. The container's policies, the name rules and the upload inspector apply as to any upload.
    /// </remarks>
    public required Func<HttpContext, ResourceUploadRequest, Task<ResourceUploadTarget?>> ResolveAsync { get; init; }

    /// <summary>
    /// Called once an upload is stored and has become its name's current version, before the client is told it
    /// finished, typically to record the file in the application's own database. If it throws, the version stays
    /// stored, the exception is logged, and the client gets 500 Internal Server Error; a client that resumes the upload
    /// later is told it finished. So save in a way that tolerates a missed call, and since two requests that complete
    /// the same upload at once each call it, make it safe to call twice for the same version. Setting it also limits
    /// <c>Upload-Metadata</c> to 4,096 characters, since the client's metadata then travels inside the upload's URL; a
    /// longer one is refused with 400 Bad Request.
    /// </summary>
    public Func<ResourceUploadCompletedContext, Task>? OnCompletedAsync { get; init; }

    /// <summary>
    /// The store uploads go to, added with <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/>;
    /// <see langword="null"/>, the default, for the store <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/>
    /// adds. Each store's upload URLs work only with its own endpoints.
    /// </summary>
    public string? StoreName { get; init; }

    /// <summary>
    /// The largest upload the endpoints take, sent to clients as a number of bytes in <c>Tus-Max-Size</c>; a larger one
    /// is refused with 413 Content Too Large before any of it is sent. <see langword="null"/>, the default, sets no
    /// limit of its own, and leaves sizes to the container's policies, which are also checked before anything is sent.
    /// Either way, no request stores more than its upload's length.
    /// </summary>
    public ResourceSize? MaxSize { get; init; }
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
    /// be missed when it throws or called twice for one version, and setting it limits <c>Upload-Metadata</c> to 4,096
    /// characters.
    /// </summary>
    public Func<ResourceUploadCompletedContext, Task>? OnCompletedAsync { get; init; }

    /// <summary>
    /// The store whose upload links the endpoints take, added with
    /// <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/> and given signed links with
    /// <see cref="ResourcefullLinksServiceCollectionExtensions.AddResourcefullLinks"/>; <see langword="null"/>, the default,
    /// for the store <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> adds.
    /// </summary>
    public string? StoreName { get; init; }

    /// <summary>
    /// The largest upload the endpoints take through any link, sent to clients as a number of bytes in
    /// <c>Tus-Max-Size</c>; a larger one is refused with 413 Content Too Large before any of it is sent.
    /// <see langword="null"/>, the default, leaves sizes to each link's <see cref="ResourceUploadLink.MaxSize"/> and the
    /// container's policies. The endpoints lift ASP.NET Core's limit on the size of a request's body, so when neither the
    /// link nor the container sets a limit, an upload has no limit at all.
    /// </summary>
    public ResourceSize? MaxSize { get; init; }
}

/// <summary>
/// A file that has just been stored, given to <see cref="ResourceUploadEndpointOptions.OnCompletedAsync"/>,
/// <see cref="ResourceUploadLinkEndpointOptions.OnCompletedAsync"/> and the <c>onStored</c> callback of
/// <c>Results.SharedUpload</c>.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceUploadCompletedContext
{
    internal ResourceUploadCompletedContext(HttpContext httpContext, ResourceInfo version, SignedResourceUploadLink? link,
        IReadOnlyDictionary<string, string>? clientMetadata)
    {
        HttpContext = httpContext;
        Version = version;
        Link = link;
        ClientMetadata = clientMetadata ?? ReadOnlyDictionary<string, string>.Empty;
    }

    /// <summary>The request that stored the file, which for a resumable upload may not be the one that started it.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>The stored version.</summary>
    public ResourceInfo Version { get; }

    /// <summary>
    /// The upload link the file came through, even if it has since expired; <see langword="null"/> for an upload through
    /// <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploads"/>, or one that started before
    /// the endpoints had an <c>OnCompletedAsync</c>.
    /// </summary>
    public SignedResourceUploadLink? Link { get; }

    /// <summary>
    /// What the client says about the file. For a resumable upload, it's the <c>Upload-Metadata</c> of the request that
    /// started the upload; for a form, the file's name in the form, as <c>filename</c>; and for a body sent with
    /// <c>PUT</c> or <c>POST</c>, it's empty, as it is for a resumable upload that started before the endpoints had an
    /// <c>OnCompletedAsync</c>. It comes from the client, so check it before using it.
    /// </summary>
    public IReadOnlyDictionary<string, string> ClientMetadata { get; }
}

/// <summary>
/// What a client asks to upload, given to <see cref="ResourceUploadEndpointOptions.ResolveAsync"/>.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed record ResourceUploadRequest
{
    /// <summary>The upload's length in bytes, from the request's <c>Upload-Length</c>.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// The request's <c>Upload-Metadata</c>, decoded: what the client says about the file, such as <c>filename</c> and
    /// <c>filetype</c>, which tus clients usually send. It comes from the client, so check it before using it.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }
}

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
/// Adds what uploads need to a CORS policy, for a page on another origin.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullUploadsCorsPolicyBuilderExtensions
{
    /// <summary>
    /// Lets a page on another origin upload through the endpoints <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploads"/>
    /// and <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploadLinks"/> map, and through
    /// upload links and forms served with <c>Results.SharedUpload</c>: adds their methods and request headers to
    /// <paramref name="policy"/>, and exposes the response headers clients read, such as <c>Location</c>,
    /// <c>Upload-Offset</c> and <c>ETag</c>. It adds to what the policy already allows. Choose the origins yourself, with
    /// <c>WithOrigins</c>.
    /// </summary>
    /// <param name="policy">The CORS policy the endpoints use.</param>
    /// <returns><paramref name="policy"/>, for chaining.</returns>
    public static CorsPolicyBuilder WithResourcefullUploadHeaders(this CorsPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy
            .WithMethods("POST", "PUT", "HEAD", "PATCH", "DELETE", "OPTIONS")
            .WithHeaders("Tus-Resumable", "Upload-Length", "Upload-Metadata", "Upload-Offset", "Content-Type", "X-HTTP-Method-Override")
            .WithExposedHeaders("Location", "Tus-Resumable", "Tus-Version", "Tus-Extension", "Tus-Max-Size", "Upload-Offset", "Upload-Length",
                "Upload-Expires", "ETag");
    }
}
