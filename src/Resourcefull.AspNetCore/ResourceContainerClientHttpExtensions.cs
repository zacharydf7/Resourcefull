using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;

namespace Resourcefull.AspNetCore;

/// <summary>Uploads a request's body, honoring its preconditions.</summary>
public static class ResourceContainerClientHttpExtensions
{
    /// <summary>
    /// Stores a request's body as the next version of <paramref name="resourceName"/>, with the request's
    /// <c>Content-Type</c>, and its <c>Content-Length</c> as the declared length so a truncated body stores nothing. The
    /// request's preconditions decide whether it's stored, race-free: <c>If-Match</c> (an entity tag from a download,
    /// several, or <c>*</c>), <c>If-None-Match</c> (<c>*</c> for "only if it doesn't exist", or entity tags), and
    /// <c>If-Unmodified-Since</c>. When the request has any, they replace <see cref="ResourceUploadOptions.Conditions"/>.
    /// Like an HTTP <c>PUT</c>, the upload replaces the current version, whatever <see cref="ResourceUploadOptions.Mode"/>
    /// says; to refuse when the name exists, the client sends
    /// <c>If-None-Match: *</c>. An endpoint that must only create can require that header, answering 428 Precondition
    /// Required without it.
    /// </summary>
    /// <param name="container">The container to store into.</param>
    /// <param name="resourceName">The resource's name.</param>
    /// <param name="request">The request whose body, headers and preconditions to use.</param>
    /// <param name="options">
    /// Metadata, the headers to store instead of the request's, and <see cref="ResourceUploadOptions.Conditions"/> for a
    /// request without preconditions. <see cref="ResourceUploadOptions.Mode"/> is ignored. The options aren't changed.
    /// </param>
    /// <param name="cancellationToken">Cancels the upload; pass the request's.</param>
    /// <returns>
    /// 201 Created when the name had no current version, otherwise 200 OK, either with the stored version's
    /// <see cref="ResourceInfo"/>, its <c>ETag</c>, and its URI as <c>Location</c> when a URI provider gives one; or
    /// 412 Precondition Failed, with the current version's <c>ETag</c>, when the preconditions don't hold.
    /// Resourcefull's other exceptions propagate; register
    /// <see cref="ResourcefullExceptionHandlerExtensions.AddResourcefullExceptionHandler"/> to turn them into status codes.
    /// </returns>
    public static async Task<IResult> UploadFromRequestAsync(this IResourceContainerClient container, string resourceName,
        HttpRequest request, ResourceUploadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(request);

        // A PUT replaces, whether or not the container keeps versions; the request's preconditions can change that.
        ResourceWriteMode mode = ResourceWriteMode.Replace;
        ResourceConditions? conditions = options?.Conditions;
        RequestHeaders headers = request.GetTypedHeaders();
        bool hasPreconditions = headers.IfMatch.Count > 0 || headers.IfNoneMatch.Count > 0 || headers.IfUnmodifiedSince is not null;

        bool existed;
        if (headers is { IfMatch: [{ IsWeak: false } tag], IfNoneMatch.Count: 0, IfUnmodifiedSince: null } && tag.Tag != "*")
        {
            // One strong tag: the write itself compares it with the current version's hash.
            conditions = new ResourceConditions { IfMatchContentHash = tag.Tag.ToString().Trim('"') };
            existed = true;
        }
        else if (headers is { IfMatch.Count: 0, IfNoneMatch: [{ Tag.Value: "*" }], IfUnmodifiedSince: null })
        {
            conditions = null;
            mode = ResourceWriteMode.Create;
            existed = false;
        }
        else
        {
            // Evaluated against the current version, then pinned to it, so the write goes ahead only if that version
            // is still current.
            ResourceInfo? current = await container.GetResourceInfoAsync(resourceName, null, cancellationToken).ConfigureAwait(false);
            existed = current is not null;
            if (hasPreconditions)
            {
                if (Preconditions.Evaluate(request, current) != StatusCodes.Status200OK)
                    return PreconditionFailed(request, current?.ContentHash, $"Resource {resourceName} doesn't meet the request's preconditions.");
                conditions = current is null ? null : new ResourceConditions { IfMatchVersionId = current.VersionId };
                mode = current is null ? ResourceWriteMode.Create : ResourceWriteMode.Replace;
            }
        }

        // A copy, so the caller's options aren't changed.
        var upload = new ResourceUploadOptions
        {
            Mode = mode,
            Conditions = conditions,
            Headers = options?.Headers ?? new ResourceHeaders
            {
                ContentType = request.ContentType,
                ContentLength = request.ContentLength
            },
            Metadata = options?.Metadata ?? new Dictionary<string, string>(),
            ExpiresAt = options?.ExpiresAt
        };
        ResourceInfo stored;
        try
        {
            stored = await container.UploadAsync(resourceName, request.Body, upload, cancellationToken).ConfigureAwait(false);
        }
        catch (ResourcePreconditionFailedException exception) when (hasPreconditions)
        {
            return PreconditionFailed(request, exception.CurrentContentHash, exception.Message);
        }
        catch (ResourceConflictException) when (hasPreconditions && mode == ResourceWriteMode.Create)
        {
            // The name exists, or was created after it was checked: If-None-Match doesn't hold. The exception's message
            // says to set Mode, which an HTTP client can't do, so the client gets a message of its own.
            return PreconditionFailed(request, null, $"Resource {resourceName} already exists.");
        }

        if (hasPreconditions) Preconditions.Record(request.HttpContext, "met");
        request.HttpContext.Response.GetTypedHeaders().ETag = Preconditions.EntityTagOf(stored);
        return existed ? TypedResults.Ok(stored) : TypedResults.Created(stored.Uri, stored);
    }

    private static IResult PreconditionFailed(HttpRequest request, string? currentContentHash, string message)
    {
        Preconditions.Record(request.HttpContext, "failed");
        request.HttpContext.Response.GetTypedHeaders().ETag = Preconditions.EntityTagOf(currentContentHash);
        return TypedResults.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: message);
    }
}
