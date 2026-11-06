using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

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
/// A <c>POST</c> of a <c>multipart/form-data</c> form, as an HTML <c>&lt;form&gt;</c> sends one, stores the files in the
/// form instead, in order: a link to one name takes one file and stores it under that name, and a link with a prefix
/// stores each file under the prefix followed by its file name, so the URL names no file. Each file is checked against
/// the link's content types and refused once it passes the link's size limit, and the first file refused stops the
/// upload; the files stored before it stay. The answer lists the files stored, by their names in the form,
/// as JSON: 201 Created with <c>{ "stored": [{ "name", "etag" }] }</c>, or the refusal's problem details with the
/// refused file's name in <c>file</c> and the files stored before it in <c>stored</c>. A form with no file, or one
/// that can't be read, gets 400.
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
        // A form's files are named in the form, so a request that names one too is for a name the link doesn't cover.
        if (check is { Status: ResourceLinkStatus.Valid, Link: { } formLink } && ResourceName is null && IsForm(request))
        {
            await StoreFormAsync(httpContext, formLink).ConfigureAwait(false);
            return;
        }
        if (check is not { Status: ResourceLinkStatus.Valid, Link: { } link } || IsForm(request) || link.NameFor(ResourceName) is not { } name)
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
                Headers = new ResourceHeaders
                {
                    ContentType = request.ContentType,
                    ContentLength = request.ContentLength
                }
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

    private static bool IsForm(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue? type)
        && type?.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Stores the files in a <c>multipart/form-data</c> form, stopping at the first one refused.</summary>
    private async Task StoreFormAsync(HttpContext httpContext, SignedResourceUploadLink link)
    {
        HttpRequest request = httpContext.Request;
        CancellationToken cancellationToken = httpContext.RequestAborted;
        var stored = new List<StoredFile>();
        string? boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(request.ContentType).Boundary).Value;
        // RFC 2046 limits a boundary to 70 characters.
        if (boundary is not { Length: > 0 and <= 70 })
        {
            await RefuseFileAsync(httpContext, StatusCodes.Status400BadRequest,
                "The form's Content-Type must have a boundary of 1 to 70 characters.", null, stored).ConfigureAwait(false);
            return;
        }

        var reader = new MultipartReader(boundary, request.Body);
        IResourceContainerClient container = Resources.GetContainerClient(link.ContainerName);
        while (true)
        {
            MultipartSection? section;
            try
            {
                section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException && !cancellationToken.IsCancellationRequested)
            {
                // The file whose headers couldn't be read has no name to give.
                await RefuseUnreadableAsync(httpContext, exception, null, stored).ConfigureAwait(false);
                return;
            }
            if (section is null) break;

            // Skips fields other than files, and file inputs left empty, which browsers send without a file name.
            if (section.GetContentDispositionHeader() is not { } disposition || !disposition.IsFileDisposition()) continue;
            string? fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (string.IsNullOrEmpty(fileName)) continue;

            if (link.ResourceName is not null && stored.Count > 0)
            {
                await RefuseFileAsync(httpContext, StatusCodes.Status400BadRequest, "The upload link takes only one file.", fileName, stored)
                    .ConfigureAwait(false);
                return;
            }
            if (TypeRefusalTitle(link, section.ContentType) is { } typeRefusal)
            {
                await RefuseFileAsync(httpContext, StatusCodes.Status415UnsupportedMediaType, typeRefusal, fileName, stored).ConfigureAwait(false);
                return;
            }

            // A form doesn't say how long each file is, so a file is cut off at the link's limit, and refused.
            var content = new ResourceUploadEndpoints.BoundedStream(section.Body, link.MaxSize ?? long.MaxValue);
            try
            {
                ResourceInfo version = await container.UploadAsync(link.ResourceName ?? link.NamePrefix + fileName, content,
                    new ResourceUploadOptions { Mode = link.Mode, Headers =
                        new ResourceHeaders { ContentType = section.ContentType } },
                    cancellationToken).ConfigureAwait(false);
                stored.Add(new StoredFile(fileName, Preconditions.EntityTagOf(version)?.ToString()));
            }
            catch (Exception) when (content.Exceeded)
            {
                await RefuseFileAsync(httpContext, StatusCodes.Status413PayloadTooLarge,
                    $"The upload link accepts at most {link.MaxSize} bytes, and {fileName} has more.", fileName, stored).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (content.ReadFailure is { } failure)
            {
                await RefuseUnreadableAsync(httpContext, failure, fileName, stored).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (RefusalTitle(exception) is { } title)
            {
                await RefuseFileAsync(httpContext, ResourcefullExceptionHandler.StatusCodeOf(exception)!.Value, title, fileName, stored)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (stored.Count == 0)
        {
            await RefuseFileAsync(httpContext, StatusCodes.Status400BadRequest, "The form has no file.", null, stored).ConfigureAwait(false);
            return;
        }
        await TypedResults.Json(new StoredFiles(stored), statusCode: StatusCodes.Status201Created).ExecuteAsync(httpContext).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a form whose reading failed with <paramref name="failure"/>: 413 Content Too Large when it's larger than
    /// the server accepts, and 400 Bad Request when it's cut short or malformed.
    /// </summary>
    private static Task RefuseUnreadableAsync(HttpContext httpContext, Exception failure, string? fileName, List<StoredFile> stored) =>
        failure is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
            ? RefuseFileAsync(httpContext, StatusCodes.Status413PayloadTooLarge, "The form is larger than this server accepts.", fileName, stored)
            : RefuseFileAsync(httpContext, StatusCodes.Status400BadRequest,
                "The form couldn't be read: it was cut short, or isn't valid multipart/form-data.", fileName, stored);

    /// <summary>
    /// Answers a form with problem details naming the file refused, if any, and the files stored before it, once the
    /// rest of the form is read and discarded: a server that answers before it has read what a browser is still
    /// sending may drop the connection, and the browser then shows an error rather than the answer.
    /// </summary>
    private static async Task RefuseFileAsync(HttpContext httpContext, int status, string title, string? fileName, List<StoredFile> stored)
    {
        try
        {
            await httpContext.Request.Body.CopyToAsync(Stream.Null, httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception) when (!httpContext.RequestAborted.IsCancellationRequested)
        {
            // What's left can't be read, such as a body past the server's size limit: answer anyway.
        }
        await TypedResults.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?>
        {
            ["file"] = fileName,
            ["stored"] = stored
        }).ExecuteAsync(httpContext).ConfigureAwait(false);
    }

    /// <summary>The files a form stored.</summary>
    private sealed record StoredFiles([property: JsonPropertyName("stored")] List<StoredFile> Stored);

    /// <summary>A file a form stored: its name in the form, and its version's <c>ETag</c>.</summary>
    private sealed record StoredFile([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("etag")] string? ETag);

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
        return TypeRefusalTitle(link, contentType) is { } title
            ? TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: title)
            : null;
    }

    /// <summary>Why the link's content types don't allow <paramref name="contentType"/>, or <see langword="null"/> if they do.</summary>
    private static string? TypeRefusalTitle(SignedResourceUploadLink link, string? contentType) =>
        new ContainerPolicy { AllowedContentTypes = link.AllowedContentTypes }.AllowsContentType(contentType)
            ? null
            : $"The upload link accepts only {string.Join(", ", link.AllowedContentTypes!)}.";

    /// <summary>
    /// Runs the result as an MVC action result, so a Razor Pages handler, which can't return an <see cref="IResult"/>,
    /// can return it.
    /// </summary>
    Task IActionResult.ExecuteResultAsync(ActionContext context) => ExecuteAsync(context.HttpContext);
}
