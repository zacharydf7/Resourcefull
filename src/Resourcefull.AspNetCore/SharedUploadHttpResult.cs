using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Stores a request's body through a signed upload link, for whoever holds the link, without authentication: the
/// token is the permission. The body is stored with the request's <c>Content-Type</c>, and its <c>Content-Length</c> as
/// the declared length, so a truncated body stores nothing. The link's mode decides whether an existing name is
/// replaced; the request's preconditions are ignored.
/// <para>
/// A valid link answers 201 Created, or 200 OK when it replaced a version, with the stored version's <c>ETag</c> and
/// no body. A token this application didn't sign, or a name the link doesn't cover, gets 404; an expired link, 410
/// Gone. Before the body is read, the link's own limits are checked: a request gets 411 Length Required when the link
/// limits the size and the request has no <c>Content-Length</c>, 413 Content Too Large when it's over that limit, and
/// 415 Unsupported Media Type when its type isn't allowed.
/// </para>
/// <para>
/// The container's policies, the name rules and the upload inspector apply as to any upload. What they refuse, a
/// create-only link's name that already exists, and a container that no longer exists get the status code
/// <see cref="ResourcefullExceptionHandler"/> would give them, with a message of its own in place of the exception's.
/// That way the holder learns nothing the link didn't give them, such as the container's name or how full it is.
/// Other exceptions propagate.
/// </para>
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class SharedUploadHttpResult : IResult, IActionResult
{
    internal SharedUploadHttpResult(IResourceServiceClient resources, string token, string? resourceName)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(token);
        Resources = resources;
        Token = token;
        ResourceName = resourceName;
    }

    /// <summary>The client the upload is stored through.</summary>
    public IResourceServiceClient Resources { get; }

    /// <summary>The token from the link.</summary>
    public string Token { get; }

    /// <summary>The name the holder gave, for a link with a prefix.</summary>
    public string? ResourceName { get; }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        HttpRequest request = httpContext.Request;
        HttpResponse response = httpContext.Response;

        IResourceLinkSigner signer = ResourceLinkServiceCollectionExtensions.SignerFor(httpContext, Resources, "Results.SharedUpload");
        ResourceUploadLinkCheck check = signer.CheckUpload(Token);
        ResourceLinkTelemetry.Record(httpContext, check.Status, Resources.StoreName);

        if (check is { Status: ResourceLinkStatus.Expired })
        {
            response.StatusCode = StatusCodes.Status410Gone;
            return;
        }
        if (check is not { Status: ResourceLinkStatus.Valid, Link: { } link } || link.NameFor(ResourceName) is not { } name)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (Refusal(link, request.ContentLength, request.ContentType) is { } refusal)
        {
            await refusal.ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }

        IResourceContainerClient container = Resources.GetContainerClient(link.ContainerName);
        CancellationToken cancellationToken = httpContext.RequestAborted;
        bool existed;
        ResourceInfo stored;
        try
        {
            // Only for the status code: a replace stores whether or not the name existed.
            existed = link.Mode == ResourceWriteMode.Replace
                && await container.ExistsAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false);
            stored = await container.UploadAsync(name, request.Body, new ResourceUploadOptions
            {
                Mode = link.Mode,
                Headers = new ResourceHeaders(request.ContentType, request.ContentLength)
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (RefusalTitle(exception) is { } title)
        {
            await TypedResults.Problem(statusCode: ResourcefullExceptionHandler.StatusCodeOf(exception), title: title)
                .ExecuteAsync(httpContext).ConfigureAwait(false);
            return;
        }

        response.GetTypedHeaders().ETag = Preconditions.EntityTagOf(stored);
        response.StatusCode = existed ? StatusCodes.Status200OK : StatusCodes.Status201Created;
    }

    /// <summary>
    /// What to tell the holder about an upload the store refused, in place of the exception's message, which names the
    /// container and can say how full it is; <see langword="null"/> for an exception that isn't a refusal.
    /// </summary>
    internal static string? RefusalTitle(Exception exception) => exception switch
    {
        ResourceConflictException => NameTaken,
        ResourceContainerNotFoundException => "The link has nowhere to store files.",
        ResourcePolicyViolationException { Rule: ResourcePolicyRule.MaxResourceSize } => "The file is larger than this store accepts.",
        ResourcePolicyViolationException { Rule: ResourcePolicyRule.MaxContainerSize } => "There's no room left for the file.",
        ResourcePolicyViolationException { Rule: ResourcePolicyRule.MaxResourceCount } => "No more files can be stored here.",
        ResourcePolicyViolationException { Rule: ResourcePolicyRule.AllowedContentTypes } => "Files of this type aren't accepted.",
        ResourcePolicyViolationException { Rule: ResourcePolicyRule.ReadOnly } => "Uploads aren't accepted here.",
        ResourceNameException => "The file's name can't be used: it's too long, starts or ends with whitespace, or has a character names can't hold.",
        ResourceContentLengthMismatchException => "The request's body is a different length than its Content-Length.",
        ResourceContentRefusedException refused => $"The file was refused: {refused.Reason}",
        _ => null
    };

    /// <summary>What a link's holder is told when the name a create-only link uploads to already has a file.</summary>
    internal const string NameTaken = "A file with this name already exists.";

    /// <summary>
    /// How to answer an upload of <paramref name="length"/> bytes of <paramref name="contentType"/> that the link's own
    /// limits refuse, or <see langword="null"/> if they allow it.
    /// </summary>
    internal static IResult? Refusal(SignedResourceUploadLink link, long? length, string? contentType)
    {
        if (link.MaxSize is { } maxSize)
        {
            if (length is null)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status411LengthRequired,
                    title: "The upload link limits the size of an upload, so the request must send Content-Length.");
            }
            if (length > maxSize)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: $"The upload link accepts at most {maxSize} bytes, and the upload has {length}.");
            }
        }
        if (!new ResourceContainerPolicy { AllowedContentTypes = link.AllowedContentTypes }.AllowsContentType(contentType))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: $"The upload link accepts only {string.Join(", ", link.AllowedContentTypes!)}.");
        }
        return null;
    }

    /// <summary>
    /// Runs the result as an MVC action result, so a Razor Pages handler, which can't return an <see cref="IResult"/>,
    /// can return it.
    /// </summary>
    Task IActionResult.ExecuteResultAsync(ActionContext context) => ExecuteAsync(context.HttpContext);
}
