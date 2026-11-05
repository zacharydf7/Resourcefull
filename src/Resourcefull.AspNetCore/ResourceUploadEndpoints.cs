using System.Buffers.Text;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Resourcefull.AspNetCore;

/// <summary>
/// Maps endpoints for resumable uploads that speak tus 1.0, the open protocol for them, over Resourcefull's upload
/// sessions, so browsers and apps can resume large uploads with tus clients such as tus-js-client and Uppy.
/// <para>Coming in 4.0.</para>
/// </summary>
public static partial class ResourcefullUploadsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps tus 1.0 endpoints at <paramref name="pattern"/>, with the creation, creation-with-upload, termination and
    /// expiration extensions: <c>POST</c> to <paramref name="pattern"/> starts an upload and answers with its URL, and
    /// <c>HEAD</c>, <c>PATCH</c> and <c>DELETE</c> on that URL tell how much is stored, append to it and cancel it. An
    /// upload completes when its last byte is stored, and its client is told it finished only once its version is
    /// stored and current.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The URL of an upload holds a token sealed with ASP.NET Core Data Protection, which names the upload and, when
    /// the request that started it was signed in, its user: it works only for that user, and only with endpoints mapped
    /// with the same <paramref name="pattern"/> string for this store, so give each set of endpoints a pattern of its
    /// own. Require authorization on the group returned, as for any endpoint that stores what users send.
    /// </para>
    /// <para>
    /// The endpoints lift ASP.NET Core's limit on the size of a request's body for their own requests, and read no
    /// more of a request than its upload has left, so no request goes past the upload's length, which
    /// <see cref="ResourceUploadEndpointOptions.MaxSize"/> and the container's size limits check when the upload starts.
    /// A server in front of the application, such as IIS or a reverse proxy, may still limit it, so have clients send
    /// uploads in chunks, such as tus-js-client's <c>chunkSize</c> option.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">Where to map the endpoints, such as <c>/uploads</c>.</param>
    /// <param name="configure">Sets <see cref="ResourceUploadEndpointOptions.ResolveAsync"/>, and any other options.</param>
    /// <returns>The group the endpoints are in, to require authorization on, and to add CORS or rate limits to.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ResourceUploadEndpointOptions.ResolveAsync"/> isn't set, or ASP.NET Core Data Protection isn't added.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceUploadEndpointOptions.MaxSize"/> is negative.</exception>
    public static RouteGroupBuilder MapResourcefullUploads(this IEndpointRouteBuilder endpoints, string pattern,
        Action<ResourceUploadEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ResourceUploadEndpointOptions();
        configure(options);
        if (options.ResolveAsync is null)
        {
            throw new InvalidOperationException(
                $"MapResourcefullUploads needs {nameof(ResourceUploadEndpointOptions.ResolveAsync)}, to decide where each upload goes.");
        }
        if (options.MaxSize is { } maxSize) ArgumentOutOfRangeException.ThrowIfNegative(maxSize, nameof(configure));
        if (options.StoreName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(options.StoreName, nameof(configure));
        if (endpoints.ServiceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(IDataProtectionProvider)) == false)
        {
            throw new InvalidOperationException(
                "MapResourcefullUploads seals upload URLs with ASP.NET Core Data Protection: call builder.Services.AddDataProtection().");
        }

        var uploads = new ResourceUploadEndpoints(options, pattern);
        RouteGroupBuilder group = endpoints.MapGroup(pattern);
        group.MapMethods("/", ["POST", "OPTIONS"], uploads.HandleCollectionAsync);
        group.MapMethods("/{token}", ["HEAD", "PATCH", "DELETE", "POST", "OPTIONS"], uploads.HandleUploadAsync);
        return group;
    }

    /// <summary>
    /// Maps tus 1.0 endpoints at <paramref name="pattern"/> for signed upload links, so whoever holds a link can upload
    /// what it allows without signing in, and resume the upload when the connection drops. A tus client's endpoint is
    /// <paramref name="pattern"/>, a <c>/</c> and the link's token, from <see cref="IResourceLinkSigner.CreateUploadToken"/>.
    /// The endpoints work as <see cref="MapResourcefullUploads"/>'s do, with the link deciding where each upload goes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starting an upload checks the link, as <c>Results.SharedUpload</c> does: an expired link gets 410 Gone, and a
    /// token this application didn't sign gets 404. Before any of the file is sent, the link's size limit is checked
    /// against the upload's length, and its content types against the <c>filetype</c> the client sends, which the
    /// version is stored with. A link to one name stores the upload under that name, and one with a prefix stores it
    /// under the prefix followed by the <c>filename</c> the client sends. The link's mode decides whether a name that
    /// already has a current version is refused or replaced, and a link can start as many uploads as it can store
    /// files with <c>Results.SharedUpload</c>.
    /// </para>
    /// <para>
    /// A link's expiry stops new uploads, not those under way: an upload started before the link expires can be
    /// resumed and finished after. With <c>UseExpiry</c>, the expiry sweep deletes it once nothing has been appended for
    /// a day, as it does any unfinished upload; without it, it stays until it's finished or canceled. What the store
    /// refuses gets the messages <c>Results.SharedUpload</c> gives, which name no container. Don't require authorization on the group returned: the link is the permission.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The application's endpoints.</param>
    /// <param name="pattern">Where to map the endpoints, such as <c>/upload-links</c>.</param>
    /// <param name="configure">
    /// Sets <see cref="ResourceUploadLinkEndpointOptions.OnCompletedAsync"/>, <see cref="ResourceUploadLinkEndpointOptions.StoreName"/>
    /// and <see cref="ResourceUploadLinkEndpointOptions.MaxSize"/>.
    /// </param>
    /// <returns>The group the endpoints are in, to add CORS or rate limits to.</returns>
    /// <exception cref="InvalidOperationException">The store's signed links aren't added with <see cref="ResourceLinkServiceCollectionExtensions.AddResourcefullLinks"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceUploadLinkEndpointOptions.MaxSize"/> is negative.</exception>
    public static RouteGroupBuilder MapResourcefullUploadLinks(this IEndpointRouteBuilder endpoints, string pattern,
        Action<ResourceUploadLinkEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        var linkOptions = new ResourceUploadLinkEndpointOptions();
        configure?.Invoke(linkOptions);
        string? storeName = linkOptions.StoreName;
        if (storeName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(storeName, nameof(configure));
        if (linkOptions.MaxSize is { } maxSize) ArgumentOutOfRangeException.ThrowIfNegative(maxSize, nameof(configure));
        IServiceProvider services = endpoints.ServiceProvider;
        bool? signs = storeName is null
            ? services.GetService<IServiceProviderIsService>()?.IsService(typeof(IResourceLinkSigner))
            : services.GetService<IServiceProviderIsKeyedService>()?.IsKeyedService(typeof(IResourceLinkSigner), storeName);
        if (signs == false)
            throw new InvalidOperationException(ResourceLinkServiceCollectionExtensions.NoSignerMessage(nameof(MapResourcefullUploadLinks), storeName));

        var options = new ResourceUploadEndpointOptions
        {
            StoreName = storeName,
            OnCompletedAsync = linkOptions.OnCompletedAsync,
            MaxSize = linkOptions.MaxSize
        };
        var uploads = new ResourceUploadEndpoints(options, pattern, throughLinks: true);
        RouteGroupBuilder group = endpoints.MapGroup(pattern);
        group.MapMethods("/{link}", ["POST", "OPTIONS"], uploads.HandleCollectionAsync);
        group.MapMethods("/{link}/{token}", ["HEAD", "PATCH", "DELETE", "POST", "OPTIONS"], uploads.HandleUploadAsync);
        return group;
    }
}

/// <summary>
/// The tus endpoints <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploads"/> maps, or,
/// when <paramref name="throughLinks"/> is true, those <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploadLinks"/>
/// maps, where the route's upload link decides where an upload goes.
/// </summary>
internal sealed partial class ResourceUploadEndpoints(ResourceUploadEndpointOptions options, string pattern, bool throughLinks = false)
{
    private const string TusVersion = "1.0.0";
    private const string OffsetOctetStream = "application/offset+octet-stream";

    // Changing them would invalidate every upload URL handed out, as removing the Data Protection keys does.
    private const string Purpose = "Resourcefull.ResourceUploadSession.v1";
    private const string LinkPurpose = "Resourcefull.ResourceUploadLinkSession.v1";

    private IDataProtector? _protector;

    /// <summary>Answers requests to the endpoints' own URL: <c>POST</c> starts an upload.</summary>
    public async Task HandleCollectionAsync(HttpContext httpContext)
    {
        switch (Begin(httpContext))
        {
            case null:
                return;
            case "OPTIONS":
                AnswerOptions(httpContext);
                return;
            case "POST":
                await CreateAsync(httpContext).ConfigureAwait(false);
                return;
            default:
                httpContext.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
        }
    }

    /// <summary>Answers requests to an upload's URL: <c>HEAD</c>, <c>PATCH</c> and <c>DELETE</c>.</summary>
    public async Task HandleUploadAsync(HttpContext httpContext)
    {
        string? method = Begin(httpContext);
        if (method is null) return;
        if (method == "OPTIONS")
        {
            AnswerOptions(httpContext);
            return;
        }
        if (method is not ("HEAD" or "PATCH" or "DELETE"))
        {
            httpContext.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        Session? session = Open(httpContext, (string)httpContext.Request.RouteValues["token"]!);
        Record(httpContext, method switch { "HEAD" => "resume", "PATCH" => "append", _ => "cancel" });
        if (session is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        try
        {
            await (method switch
            {
                "HEAD" => ResumeAsync(httpContext, session),
                "PATCH" => AppendAsync(httpContext, session),
                _ => CancelAsync(httpContext, session)
            }).ConfigureAwait(false);
        }
        catch (Exception) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away, as it does when its connection drops: what was stored stays, for it to resume.
        }
    }

    /// <summary>
    /// Starts a response: sets <c>Tus-Resumable</c>, and checks the request's, except on <c>OPTIONS</c>.
    /// </summary>
    /// <returns>The request's method, as overridden by <c>X-HTTP-Method-Override</c>; <see langword="null"/> once answered.</returns>
    private static string? Begin(HttpContext httpContext)
    {
        HttpRequest request = httpContext.Request;
        HttpResponse response = httpContext.Response;
        response.Headers["Tus-Resumable"] = TusVersion;
        string method = request.Headers["X-HTTP-Method-Override"] is [{ Length: > 0 } overridden]
            ? overridden.ToUpperInvariant()
            : request.Method;
        if (method == "OPTIONS" || request.Headers["Tus-Resumable"] == TusVersion) return method;

        response.Headers["Tus-Version"] = TusVersion;
        response.StatusCode = StatusCodes.Status412PreconditionFailed;
        return null;
    }

    private void AnswerOptions(HttpContext httpContext)
    {
        Record(httpContext, "options");
        HttpResponse response = httpContext.Response;
        response.Headers["Tus-Version"] = TusVersion;
        response.Headers["Tus-Extension"] = "creation,creation-with-upload,termination,expiration";
        if (options.MaxSize is { } maxSize) response.Headers["Tus-Max-Size"] = maxSize.ToString(CultureInfo.InvariantCulture);
        response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>Starts an upload, with any content the request carries, which may complete it.</summary>
    private async Task CreateAsync(HttpContext httpContext)
    {
        Record(httpContext, "create");
        HttpRequest request = httpContext.Request;
        HttpResponse response = httpContext.Response;
        if (request.Headers.ContainsKey("Upload-Defer-Length"))
        {
            await RefuseAsync(httpContext, StatusCodes.Status400BadRequest,
                "Uploads must say their length in Upload-Length: deferred lengths aren't supported.").ConfigureAwait(false);
            return;
        }
        if (!TryParseLength(request.Headers["Upload-Length"], out long length))
        {
            await RefuseAsync(httpContext, StatusCodes.Status400BadRequest, "Upload-Length must be a whole number of bytes.").ConfigureAwait(false);
            return;
        }
        if (length > options.MaxSize)
        {
            await RefuseAsync(httpContext, StatusCodes.Status413PayloadTooLarge,
                $"The upload is larger than the {options.MaxSize} bytes these endpoints take.").ConfigureAwait(false);
            return;
        }
        if (!TryParseMetadata(request.Headers["Upload-Metadata"], out Dictionary<string, string>? metadata))
        {
            await RefuseAsync(httpContext, StatusCodes.Status400BadRequest,
                "Upload-Metadata must be pairs of a key and its Base64 value, separated by commas.").ConfigureAwait(false);
            return;
        }
        // Content is recognized by its tus content type rather than its Content-Length, which HTTP/2 may leave out.
        bool hasContent = IsOffsetOctetStream(request) && request.ContentLength != 0;
        if (!hasContent && request.ContentLength > 0)
        {
            await RefuseAsync(httpContext, StatusCodes.Status415UnsupportedMediaType,
                $"Content sent with the request that starts an upload must be {OffsetOctetStream}.").ConfigureAwait(false);
            return;
        }
        if (request.ContentLength > length)
        {
            await RefuseAsync(httpContext, StatusCodes.Status413PayloadTooLarge, TooMuchContent).ConfigureAwait(false);
            return;
        }

        ResourceUploadTarget? target;
        if (throughLinks)
        {
            target = await LinkTargetAsync(httpContext, length, metadata).ConfigureAwait(false);
            if (target is null) return;
        }
        else if ((target = await options.ResolveAsync!(httpContext, new ResourceUploadRequest(length, metadata)).ConfigureAwait(false)) is null)
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        IResourceContainerClient container = Resources(httpContext).GetContainerClient(target.ContainerName);
        CancellationToken cancellationToken = httpContext.RequestAborted;
        ResourceUpload upload;
        try
        {
            if (target.Mode == ResourceWriteMode.Create
                && await container.GetResourceInfoAsync(target.ResourceName, cancellationToken: cancellationToken).ConfigureAwait(false) is not null)
            {
                await RefuseAsync(httpContext, StatusCodes.Status409Conflict, throughLinks
                    ? SharedUploadHttpResult.NameTaken
                    : $"The resource {target.ResourceName} already exists in the container {target.ContainerName}.").ConfigureAwait(false);
                return;
            }
            upload = await container.StartUploadAsync(target.ResourceName, new ResourceUploadSessionOptions
            {
                Headers = new ResourceHeaders(target.ContentType, length),
                Metadata = target.Metadata,
                ExpiresAt = target.ExpiresAt,
                RetainUntil = target.RetainUntil,
                LegalHold = target.LegalHold
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ResourcefullExceptionHandler.StatusCodeOf(exception) is { } status)
        {
            await RefuseAsync(httpContext, status, exception).ConfigureAwait(false);
            return;
        }

        // The link is the permission, so an upload through one works for whoever has its URL, signed in or not.
        var session = new Session(target.ContainerName, target.ResourceName, upload.UploadId, length,
            target.Mode == ResourceWriteMode.Replace, throughLinks ? null : UserOf(httpContext.User));
        response.Headers.Location = $"{request.PathBase}{request.Path.Value!.TrimEnd('/')}/{Seal(httpContext, session)}";
        long offset = 0;
        if (hasContent)
        {
            LiftBodySizeLimit(httpContext);
            var content = new BoundedStream(request.Body, length);
            ResourceUpload? appended;
            try
            {
                appended = await container.AppendUploadAsync(session.ResourceName, session.UploadId, 0, content, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // The client went away; it resumes from the URL it was given, or starts again.
                return;
            }
            catch (Exception exception) when (RefusalOf(content, exception) is { } status)
            {
                // The client is told the upload failed, so it won't use the upload's URL: nothing would finish it.
                await container.CancelUploadAsync(session.ResourceName, session.UploadId, CancellationToken.None).ConfigureAwait(false);
                response.Headers.Location = default;
                await RefuseAsync(httpContext, status, content, exception).ConfigureAwait(false);
                return;
            }
            if (appended is null)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            offset = appended.Offset;
        }
        if (offset >= length)
        {
            Record(httpContext, "complete");
            if (!await CompleteAsync(httpContext, container, session).ConfigureAwait(false)) return;
        }

        if (hasContent || length == 0) response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
        if (offset < length) SetExpiry(httpContext);
        response.StatusCode = StatusCodes.Status201Created;
    }

    /// <summary>
    /// Tells how much of an upload is stored. A session that holds all of it but isn't completed, because its
    /// completion failed, is completed now, so a client isn't told it finished before its version is stored.
    /// </summary>
    private async Task ResumeAsync(HttpContext httpContext, Session session)
    {
        HttpResponse response = httpContext.Response;
        response.Headers.CacheControl = "no-store";
        IResourceContainerClient container = Resources(httpContext).GetContainerClient(session.ContainerName);
        ResourceUpload? upload = await container.GetUploadAsync(session.ResourceName, session.UploadId, httpContext.RequestAborted)
            .ConfigureAwait(false);
        if (upload is null)
        {
            if (!await IsStoredAsync(container, session, httpContext.RequestAborted).ConfigureAwait(false))
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        else if (upload.Offset >= session.Length)
        {
            Record(httpContext, "complete");
            if (!await CompleteAsync(httpContext, container, session, resuming: true).ConfigureAwait(false)) return;
        }

        long offset = upload is null || upload.Offset >= session.Length ? session.Length : upload.Offset;
        response.Headers["Upload-Offset"] = offset.ToString(CultureInfo.InvariantCulture);
        response.Headers["Upload-Length"] = session.Length.ToString(CultureInfo.InvariantCulture);
        response.StatusCode = StatusCodes.Status200OK;
    }

    /// <summary>Appends the request's content, completing the upload once all of it is stored.</summary>
    private async Task AppendAsync(HttpContext httpContext, Session session)
    {
        HttpRequest request = httpContext.Request;
        HttpResponse response = httpContext.Response;
        if (!IsOffsetOctetStream(request))
        {
            await RefuseAsync(httpContext, StatusCodes.Status415UnsupportedMediaType, $"Content must be sent as {OffsetOctetStream}.")
                .ConfigureAwait(false);
            return;
        }
        if (!TryParseLength(request.Headers["Upload-Offset"], out long offset))
        {
            await RefuseAsync(httpContext, StatusCodes.Status400BadRequest, "Upload-Offset must be a whole number of bytes.").ConfigureAwait(false);
            return;
        }

        if (offset + request.ContentLength > session.Length)
        {
            await RefuseAsync(httpContext, StatusCodes.Status413PayloadTooLarge, TooMuchContent).ConfigureAwait(false);
            return;
        }

        LiftBodySizeLimit(httpContext);
        IResourceContainerClient container = Resources(httpContext).GetContainerClient(session.ContainerName);
        var content = new BoundedStream(request.Body, Math.Max(session.Length - offset, 0));
        ResourceUpload? upload;
        try
        {
            upload = await container.AppendUploadAsync(session.ResourceName, session.UploadId, offset, content, httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!httpContext.RequestAborted.IsCancellationRequested && RefusalOf(content, exception) is { } status)
        {
            // When a request is cut off at the upload's length, what the provider stored of it stays, as after a dropped
            // connection.
            await RefuseAsync(httpContext, status, content, exception).ConfigureAwait(false);
            return;
        }
        if (upload is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (upload.Offset >= session.Length)
        {
            Record(httpContext, "complete");
            if (!await CompleteAsync(httpContext, container, session).ConfigureAwait(false)) return;
        }
        else
        {
            SetExpiry(httpContext);
        }

        response.Headers["Upload-Offset"] = upload.Offset.ToString(CultureInfo.InvariantCulture);
        response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task CancelAsync(HttpContext httpContext, Session session)
    {
        IResourceContainerClient container = Resources(httpContext).GetContainerClient(session.ContainerName);
        httpContext.Response.StatusCode = await container.CancelUploadAsync(session.ResourceName, session.UploadId, httpContext.RequestAborted)
            .ConfigureAwait(false)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status404NotFound;
    }

    /// <summary>
    /// Completes an upload that holds all its content, then calls <see cref="ResourceUploadEndpointOptions.OnCompletedAsync"/>.
    /// A refused completion cancels the session, so a client that retries is told the upload is gone, rather than that
    /// it finished. One that fails otherwise keeps it, and answers 503 Service Unavailable, or, when
    /// <paramref name="resuming"/>, 423 Locked: a tus client that resumes and gets any other error starts the upload
    /// again from the beginning, while it retries a locked one. Completing isn't canceled with the request, so a client
    /// that gives up while a large upload is read and checked finds it completed when it resumes. When
    /// <see cref="ResourceUploadEndpointOptions.OnCompletedAsync"/> throws, the version is stored, so a client that
    /// resumes is told the upload finished, and one whose last request completed it gets 500 Internal Server Error.
    /// </summary>
    /// <returns>Whether the upload's version is stored and the response can go on; when not, it's answered.</returns>
    private async Task<bool> CompleteAsync(HttpContext httpContext, IResourceContainerClient container, Session session,
        bool resuming = false)
    {
        ResourceInfo? version;
        try
        {
            version = await container.CompleteUploadAsync(session.ResourceName, session.UploadId,
                session.Replace ? ResourceWriteMode.Replace : ResourceWriteMode.Create, cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ResourceUploadOffsetException exception)
        {
            // Another append stored more while the content was read; the session stays, to be completed again.
            await RefuseAsync(httpContext, StatusCodes.Status409Conflict, exception).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (ResourcefullExceptionHandler.StatusCodeOf(exception) is { } status)
        {
            await container.CancelUploadAsync(session.ResourceName, session.UploadId, CancellationToken.None).ConfigureAwait(false);
            await RefuseAsync(httpContext, status, exception).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCompletionFailed(Logger(httpContext), exception, session.UploadId, session.ResourceName, session.ContainerName);
            httpContext.Response.Headers.RetryAfter = "1";
            await RefuseAsync(httpContext, resuming ? StatusCodes.Status423Locked : StatusCodes.Status503ServiceUnavailable,
                "The upload couldn't be completed just now. Resume it to try again.").ConfigureAwait(false);
            return false;
        }
        if (version is null)
        {
            // Completed by another request meanwhile, which called OnCompletedAsync, or canceled.
            if (await IsStoredAsync(container, session, CancellationToken.None).ConfigureAwait(false)) return true;
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return false;
        }

        if (options.OnCompletedAsync is { } onCompleted)
        {
            try
            {
                await onCompleted(httpContext, version).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogOnCompletedFailed(Logger(httpContext), exception, session.UploadId, session.ResourceName, session.ContainerName);
                // An error would make a client that resumes start the upload again, though it's stored.
                if (resuming) return true;
                httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether the upload's version is stored: completed, so the session is gone.</summary>
    private static async Task<bool> IsStoredAsync(IResourceContainerClient container, Session session, CancellationToken cancellationToken) =>
        // An upload's ID is its version's ID.
        await container.GetResourceInfoAsync(session.ResourceName, session.UploadId, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Where an upload through the route's link goes, once the link is checked as <c>Results.SharedUpload</c> checks it,
    /// and what the upload declares is checked against what the link allows; <see langword="null"/> once a refusal is
    /// answered.
    /// </summary>
    private async Task<ResourceUploadTarget?> LinkTargetAsync(HttpContext httpContext, long length, IReadOnlyDictionary<string, string> metadata)
    {
        IResourceServiceClient resources = Resources(httpContext);
        ResourceUploadLinkCheck check = ResourceLinkServiceCollectionExtensions.SignerFor(httpContext, resources, Caller)
            .CheckUpload((string)httpContext.Request.RouteValues["link"]!);
        ResourceLinkTelemetry.Record(httpContext, check.Status, resources.StoreName);
        if (check is not { Status: ResourceLinkStatus.Valid, Link: { } link })
        {
            httpContext.Response.StatusCode = check.Status == ResourceLinkStatus.Expired ? StatusCodes.Status410Gone : StatusCodes.Status404NotFound;
            return null;
        }

        // tus clients send the file's name whatever the link, so a link to one name ignores it.
        if ((link.ResourceName ?? link.NameFor(metadata.GetValueOrDefault("filename"))) is not { } name)
        {
            await RefuseAsync(httpContext, StatusCodes.Status400BadRequest,
                "The upload link needs each file's name: send it as filename in Upload-Metadata.").ConfigureAwait(false);
            return null;
        }
        // Unlike a Content-Type header, tus metadata can hold any text, and a filetype that isn't a valid media type
        // would fail every download of the file.
        string? contentType = metadata.GetValueOrDefault("filetype") is { Length: > 0 } type ? type : null;
        if (contentType is not null && (contentType.Any(c => c is < ' ' or > '~') || !MediaTypeHeaderValue.TryParse(contentType, out _)))
        {
            await RefuseAsync(httpContext, StatusCodes.Status400BadRequest, "The filetype in Upload-Metadata must be a media type, such as image/png.")
                .ConfigureAwait(false);
            return null;
        }
        if (SharedUploadHttpResult.Refusal(link, length, contentType) is { } refusal)
        {
            await refusal.ExecuteAsync(httpContext).ConfigureAwait(false);
            return null;
        }
        return new ResourceUploadTarget(link.ContainerName, name) { Mode = link.Mode, ContentType = contentType };
    }

    /// <summary>The method that mapped the endpoints, for error messages.</summary>
    private string Caller => throughLinks
        ? nameof(ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploadLinks)
        : nameof(ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploads);

    private IResourceServiceClient Resources(HttpContext httpContext) =>
        options.StoreName is not { } storeName
            ? httpContext.RequestServices.GetRequiredService<IResourceServiceClient>()
            : httpContext.RequestServices.GetKeyedService<IResourceServiceClient>(storeName)
                ?? throw new InvalidOperationException(
                    $"{Caller} needs a store named '{storeName}' added with {nameof(ResourcefullServiceCollectionExtensions.AddKeyedResourcefull)}.");

    /// <summary>
    /// Sets <c>Upload-Expires</c> to when the expiry sweep deletes the upload if nothing more is appended, when the
    /// store runs it.
    /// </summary>
    private void SetExpiry(HttpContext httpContext)
    {
        ResourcefullOptions settings = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<ResourcefullOptions>>()
            .Get(options.StoreName ?? Options.DefaultName);
        if (settings.AbandonedUploadsDeletedAfter is not { } after) return;
        DateTimeOffset now = (httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        httpContext.Response.Headers["Upload-Expires"] = (now + after).ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Tags the request's span with what it did, and the store, for a named one.</summary>
    private void Record(HttpContext httpContext, string action)
    {
        if (httpContext.Features.Get<IHttpActivityFeature>()?.Activity is not { } activity) return;
        activity.SetTag("resourcefull.tus", action);
        if (options.StoreName is not null) activity.SetTag("resourcefull.store", options.StoreName);
    }

    /// <summary>Answers with <paramref name="status"/> and, except to <c>HEAD</c>, problem details titled <paramref name="title"/>.</summary>
    private static async Task RefuseAsync(HttpContext httpContext, int status, string title, Exception? exception = null)
    {
        httpContext.Response.StatusCode = status;
        if (HttpMethods.IsHead(httpContext.Request.Method)
            || httpContext.RequestServices.GetService<IProblemDetailsService>() is not { } problemDetails)
        {
            return;
        }
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers with <paramref name="status"/> for what the store refused, titled with the exception's message or,
    /// through links, with one of <c>Results.SharedUpload</c>'s, which don't name the container or say how full it is.
    /// </summary>
    private Task RefuseAsync(HttpContext httpContext, int status, Exception exception) => throughLinks
        ? RefuseAsync(httpContext, status, SharedUploadHttpResult.RefusalTitle(exception) ?? exception switch
        {
            ResourceUploadOffsetException => "The upload holds a different number of bytes than the request says. Resume it to find out how many.",
            _ => "The upload couldn't be stored."
        })
        : RefuseAsync(httpContext, status, exception.Message, exception);

    /// <summary>Answers with <paramref name="status"/> for an append that failed while reading <paramref name="content"/>.</summary>
    private Task RefuseAsync(HttpContext httpContext, int status, BoundedStream content, Exception exception) => content.Exceeded
        ? RefuseAsync(httpContext, status, TooMuchContent, exception)
        : RefuseAsync(httpContext, status, exception);

    /// <summary>
    /// Lets the request's body be as large as its upload: the endpoints read no more of it than the upload has left.
    /// </summary>
    private static void LiftBodySizeLimit(HttpContext httpContext)
    {
        if (httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = null;
    }

    private const string TooMuchContent = "The request sends more content than the upload has left.";

    /// <summary>
    /// The status a failed append is refused with: 413 Content Too Large when the request held more than its upload has
    /// left, the status <see cref="ResourcefullExceptionHandler"/> gives Resourcefull's exceptions, or none.
    /// </summary>
    private static int? RefusalOf(BoundedStream content, Exception exception) =>
        content.Exceeded ? StatusCodes.Status413PayloadTooLarge : ResourcefullExceptionHandler.StatusCodeOf(exception);

    private static bool IsOffsetOctetStream(HttpRequest request) =>
        string.Equals(request.ContentType, OffsetOctetStream, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseLength(string? value, out long length) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out length);

    /// <summary>Reads <c>Upload-Metadata</c>: comma-separated pairs of a key and, after a space, its value in Base64.</summary>
    internal static bool TryParseMetadata(string? header, out Dictionary<string, string> metadata)
    {
        metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(header)) return true;
        foreach (string pair in header.Split(','))
        {
            string[] parts = pair.Trim().Split(' ');
            if (parts.Length > 2 || parts[0].Length == 0) return false;
            string value = "";
            if (parts.Length == 2)
            {
                byte[] bytes = new byte[Base64.GetMaxDecodedFromUtf8Length(parts[1].Length)];
                if (!Convert.TryFromBase64String(parts[1], bytes, out int written)) return false;
                try
                {
                    value = new UTF8Encoding(false, true).GetString(bytes, 0, written);
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
            }
            if (!metadata.TryAdd(parts[0], value)) return false;
        }
        return true;
    }

    private string Seal(HttpContext httpContext, Session session) =>
        Base64Url.EncodeToString(Protector(httpContext).Protect(JsonSerializer.SerializeToUtf8Bytes(session)));

    /// <summary>The upload a token names, or <see langword="null"/> if it isn't one these endpoints sealed, or it's another user's.</summary>
    private Session? Open(HttpContext httpContext, string token) =>
        ResourceLinkSigner.Open<Session>(Protector(httpContext), token) is { } session
            && (session.User is null || session.User == UserOf(httpContext.User))
            ? session
            : null;

    private IDataProtector Protector(HttpContext httpContext)
    {
        if (_protector is { } protector) return protector;
        IDataProtectionProvider provider = httpContext.RequestServices.GetRequiredService<IDataProtectionProvider>();
        // The purpose adds the endpoints' pattern and, for a named store, its name, so an upload's URL works only with
        // endpoints mapped with the same pattern string for the same store, whose ResolveAsync and OnCompletedAsync are
        // those that chose its target and expect it. Uploads through links have a purpose of their own.
        string purpose = throughLinks ? LinkPurpose : Purpose;
        return _protector = options.StoreName is null
            ? provider.CreateProtector(purpose, pattern)
            : provider.CreateProtector(purpose, pattern, options.StoreName);
    }

    /// <summary>
    /// A short hash of the signed-in user's ID, from <c>NameIdentifier</c>, <c>sub</c> or the name, so an upload's URL
    /// works only for the user who started it; <see langword="null"/> when the request isn't signed in.
    /// </summary>
    private static string? UserOf(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        string? id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.Identity.Name;
        return id is null ? null : Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(id)).AsSpan(0, 16));
    }

    private static ILogger Logger(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Resourcefull.AspNetCore.Uploads");

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Completing upload {UploadId} of resource {ResourceName} in container {ContainerName} failed; it's kept, for the client to resume.")]
    private static partial void LogCompletionFailed(ILogger logger, Exception exception, string uploadId, string resourceName, string containerName);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "OnCompletedAsync failed for upload {UploadId} of resource {ResourceName} in container {ContainerName}, which is stored.")]
    private static partial void LogOnCompletedFailed(ILogger logger, Exception exception, string uploadId, string resourceName, string containerName);

    /// <summary>
    /// Reads no more than <paramref name="length"/> bytes of <paramref name="inner"/>, and throws if it holds more,
    /// setting <see cref="Exceeded"/>, so a request can't store more than its upload has left.
    /// </summary>
    private sealed class BoundedStream(Stream inner, long length) : Stream
    {
        private long _left = length;

        /// <summary>Whether the request held more than its upload has left.</summary>
        public bool Exceeded { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // With nothing left, reading one more byte tells a request that ends there from one that goes on.
            int read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Math.Max(_left, 1))], cancellationToken).ConfigureAwait(false);
            if (read > _left)
            {
                Exceeded = true;
                throw new IOException(TooMuchContent);
            }
            _left -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>What an upload's token holds, with short names to keep its URL short.</summary>
    private sealed record Session(
        [property: JsonPropertyName("c")] string ContainerName,
        [property: JsonPropertyName("n")] string ResourceName,
        [property: JsonPropertyName("u")] string UploadId,
        [property: JsonPropertyName("l")] long Length,
        [property: JsonPropertyName("r")] bool Replace,
        [property: JsonPropertyName("w")] string? User);
}
