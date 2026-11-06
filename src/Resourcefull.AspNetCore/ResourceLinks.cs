using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Resourcefull.AspNetCore;

/// <summary>
/// What a signed link to a resource lets its holder download, and for how long. Pass it to
/// <see cref="IResourceLinkSigner.CreateDownloadTokenAsync"/>.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The container the resource is in.</param>
/// <param name="ResourceName">The resource to share.</param>
public sealed record ResourceDownloadLink(string ContainerName, string ResourceName)
{
    /// <summary>How long the link works, from when it's created: one hour unless you say otherwise.</summary>
    public TimeSpan ExpiresIn { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The version the link shows. When <see langword="null"/>, it's the version that's current when the link is
    /// created, so a later upload doesn't change what the link shows, unless <see cref="FollowCurrent"/> is set.
    /// </summary>
    public string? VersionId { get; init; }

    /// <summary>
    /// Whether the link shows whichever version is current each time it's opened, rather than one version. It then
    /// grants access to whatever the name holds until the link expires. It can't be combined with <see cref="VersionId"/>.
    /// </summary>
    public bool FollowCurrent { get; init; }

    /// <summary>
    /// Whether browsers should display the file (<c>Content-Disposition: inline</c>) rather than download it. It's
    /// decided when the link is opened, from the file's type then: only
    /// <see cref="ResourceResultOptions.DefaultInlineContentTypes"/> are displayed, and anything else is downloaded.
    /// </summary>
    public bool Inline { get; init; }
}

/// <summary>
/// What a valid download token lets its holder download.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed record SignedResourceDownloadLink
{
    /// <summary>The container the resource is in.</summary>
    public required string ContainerName { get; init; }

    /// <summary>The resource's name.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The version's ID, or <see langword="null"/> for whichever is current.</summary>
    public string? VersionId { get; init; }

    /// <summary>When the link stops working.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Whether browsers should display the file rather than download it.</summary>
    public required bool Inline { get; init; }
}

/// <summary>
/// Whether a token is a link that still works.
/// <para>Coming in 4.0.</para>
/// </summary>
public enum ResourceLinkStatus
{
    /// <summary>Signed by this application and not yet expired.</summary>
    Valid,

    /// <summary>Signed by this application, but past its expiry.</summary>
    Expired,

    /// <summary>
    /// Not a token this application signed: made up, changed, cut short, or signed with a key that's since been removed.
    /// </summary>
    Invalid
}

/// <summary>
/// The result of checking a download token.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed record ResourceDownloadLinkCheck
{
    /// <summary>Whether the token is a link that still works.</summary>
    public required ResourceLinkStatus Status { get; init; }

    /// <summary>What a valid or expired token grants; <see langword="null"/> for an invalid one.</summary>
    public SignedResourceDownloadLink? Link { get; init; }
}

/// <summary>
/// Creates and checks the tokens of signed links: strings that let whoever holds them download one resource, or upload
/// what an upload link allows, until they expire, without signing in. Register it with <see cref="ResourcefullLinksServiceCollectionExtensions.AddResourcefullLinks"/>.
/// Tokens are signed and encrypted with ASP.NET Core Data Protection, so they can't be changed, read or made up, and
/// they work on every instance that shares the application's Data Protection keys. Each store has a signer of its own,
/// and a token made by one store's signer doesn't work with another store.
/// <para>Coming in 4.0.</para>
/// </summary>
public interface IResourceLinkSigner
{
    /// <summary>
    /// Creates a token for a download <paramref name="link"/>, to put into a URL your application serves with
    /// <c>Results.SharedDownload</c>. Anyone with the URL can download the resource until the link expires.
    /// </summary>
    /// <param name="link">What to share, and for how long.</param>
    /// <param name="cancellationToken">Cancels looking the resource up.</param>
    /// <returns>The token, or <see langword="null"/> if the resource (or the version asked for) doesn't exist.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceDownloadLink.ExpiresIn"/> isn't positive.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceDownloadLink.FollowCurrent"/> is set with a <see cref="ResourceDownloadLink.VersionId"/>, or a name breaks the
    /// name rules.
    /// </exception>
    Task<string?> CreateDownloadTokenAsync(ResourceDownloadLink link, CancellationToken cancellationToken = default);

    /// <summary>Checks a download token: whether this application signed it, whether it has expired, and what it grants.</summary>
    /// <param name="token">The token from the link's URL.</param>
    ResourceDownloadLinkCheck CheckDownload(string token);

    /// <summary>
    /// Creates a token for an upload <paramref name="link"/>, to put into a URL your application serves with
    /// <c>Results.SharedUpload</c>, or to add after the pattern
    /// <see cref="ResourcefullUploadsEndpointRouteBuilderExtensions.MapResourcefullUploadLinks"/> maps, for resumable
    /// uploads. Anyone with the URL can upload what the link allows until it expires. Upload tokens and download tokens
    /// are sealed apart, so neither works as the other.
    /// </summary>
    /// <param name="link">Where uploads go, what they may be, and for how long.</param>
    /// <param name="cancellationToken">Cancels looking the container up.</param>
    /// <returns>The token, or <see langword="null"/> if the container doesn't exist.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceUploadLink.ExpiresIn"/> isn't positive.</exception>
    /// <exception cref="ArgumentException">
    /// The link sets both <see cref="ResourceUploadLink.ResourceName"/> and <see cref="ResourceUploadLink.NamePrefix"/>, or
    /// neither, or a name breaks the name rules.
    /// </exception>
    Task<string?> CreateUploadTokenAsync(ResourceUploadLink link, CancellationToken cancellationToken = default);

    /// <summary>Checks an upload token: whether this application signed it, whether it has expired, and what it allows.</summary>
    /// <param name="token">The token from the link's URL.</param>
    ResourceUploadLinkCheck CheckUpload(string token);
}

/// <summary>Signs links with the application's Data Protection keys.</summary>
internal sealed class ResourceLinkSigner(IDataProtectionProvider dataProtection, IResourceServiceClient resources,
    TimeProvider? timeProvider = null) : IResourceLinkSigner
{
    // Changing a purpose would invalidate every link ever made with it, as removing the keys does. A named store's
    // purposes add its name, so its tokens don't open with another store's signer.
    private readonly IDataProtector _protector = Protector(dataProtection, "Resourcefull.ResourceLink.v1", resources.StoreName);
    private readonly IDataProtector _uploadProtector = Protector(dataProtection, "Resourcefull.ResourceUploadLink.v1", resources.StoreName);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<string?> CreateDownloadTokenAsync(ResourceDownloadLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(link.ExpiresIn, TimeSpan.Zero, nameof(link));
        if (link.FollowCurrent && link.VersionId is not null)
            throw new ArgumentException("A link can show one version or follow the current one, not both.", nameof(link));

        ResourceInfo? version = await resources.GetContainerClient(link.ContainerName)
            .GetResourceInfoAsync(link.ResourceName, link.VersionId, cancellationToken).ConfigureAwait(false);
        if (version is null) return null;

        var payload = new Payload(link.ContainerName, link.ResourceName, link.FollowCurrent ? null : version.VersionId,
            (_clock.GetUtcNow() + link.ExpiresIn).ToUnixTimeMilliseconds(), link.Inline);
        return Base64Url.EncodeToString(_protector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    public ResourceDownloadLinkCheck CheckDownload(string token)
    {
        if (Open<Payload>(_protector, token) is not { } payload)
            return new ResourceDownloadLinkCheck { Status = ResourceLinkStatus.Invalid };
        var link = new SignedResourceDownloadLink
        {
            ContainerName = payload.ContainerName,
            ResourceName = payload.ResourceName,
            VersionId = payload.VersionId,
            ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresAt),
            Inline = payload.Inline
        };
        return new ResourceDownloadLinkCheck { Status = StatusAt(link.ExpiresAt), Link = link };
    }

    public async Task<string?> CreateUploadTokenAsync(ResourceUploadLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(link.ExpiresIn, TimeSpan.Zero, nameof(link));
        if (link.ResourceName is null == link.NamePrefix is null)
            throw new ArgumentException("An upload link uploads to one name or to names under a prefix: set one of the two.", nameof(link));
        // A prefix is valid when a name can start with it.
        if (!ResourceNames.IsValidContainerName(link.ContainerName)
            || !ResourceNames.IsValidResourceName(link.ResourceName ?? link.NamePrefix + "a"))
        {
            throw new ArgumentException("The upload link's container name, resource name or prefix breaks the name rules.", nameof(link));
        }
        if (!await resources.ContainerExistsAsync(link.ContainerName, cancellationToken).ConfigureAwait(false)) return null;

        var payload = new UploadPayload(link.ContainerName, link.ResourceName, link.NamePrefix, link.MaxSize?.Bytes,
            link.AllowedContentTypes?.ToArray(), link.Mode == ResourceWriteMode.Replace,
            (_clock.GetUtcNow() + link.ExpiresIn).ToUnixTimeMilliseconds());
        return Base64Url.EncodeToString(_uploadProtector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    public ResourceUploadLinkCheck CheckUpload(string token)
    {
        if (Open<UploadPayload>(_uploadProtector, token) is not { } payload)
            return new ResourceUploadLinkCheck { Status = ResourceLinkStatus.Invalid };
        var link = new SignedResourceUploadLink
        {
            ContainerName = payload.ContainerName,
            ResourceName = payload.ResourceName,
            NamePrefix = payload.NamePrefix,
            MaxSize = payload.MaxSize,
            AllowedContentTypes = payload.AllowedContentTypes,
            Mode = payload.Replace ? ResourceWriteMode.Replace : ResourceWriteMode.Create,
            ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresAt)
        };
        return new ResourceUploadLinkCheck { Status = StatusAt(link.ExpiresAt), Link = link };
    }

    /// <summary>What <paramref name="token"/> holds, or <see langword="null"/> if this application didn't seal it.</summary>
    internal static T? Open<T>(IDataProtector protector, string token) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(protector.Unprotect(Base64Url.DecodeFromChars(token)));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            // Not something this application signed, whatever is wrong with it.
            return null;
        }
    }

    private static IDataProtector Protector(IDataProtectionProvider dataProtection, string purpose, string? storeName) =>
        storeName is null ? dataProtection.CreateProtector(purpose) : dataProtection.CreateProtector(purpose, storeName);

    private ResourceLinkStatus StatusAt(DateTimeOffset expiresAt) =>
        expiresAt <= _clock.GetUtcNow() ? ResourceLinkStatus.Expired : ResourceLinkStatus.Valid;

    /// <summary>What a token holds, with short names to keep links short.</summary>
    private sealed record Payload(
        [property: JsonPropertyName("c")] string ContainerName,
        [property: JsonPropertyName("n")] string ResourceName,
        [property: JsonPropertyName("v")] string? VersionId,
        [property: JsonPropertyName("e")] long ExpiresAt,
        [property: JsonPropertyName("i")] bool Inline);

    /// <summary>What an upload token holds, with short names to keep links short.</summary>
    private sealed record UploadPayload(
        [property: JsonPropertyName("c")] string ContainerName,
        [property: JsonPropertyName("n")] string? ResourceName,
        [property: JsonPropertyName("p")] string? NamePrefix,
        [property: JsonPropertyName("s")] long? MaxSize,
        [property: JsonPropertyName("t")] string[]? AllowedContentTypes,
        [property: JsonPropertyName("r")] bool Replace,
        [property: JsonPropertyName("e")] long ExpiresAt);
}

/// <summary>Tags the request's span with whether its link works, and which named store it's for.</summary>
internal static class ResourceLinkTelemetry
{
    public static void Record(HttpContext httpContext, ResourceLinkStatus status, string? storeName)
    {
        if (httpContext.Features.Get<IHttpActivityFeature>()?.Activity is not { } activity) return;
        activity.SetTag("resourcefull.link", status switch
        {
            ResourceLinkStatus.Valid => "valid",
            ResourceLinkStatus.Expired => "expired",
            _ => "invalid"
        });
        if (storeName is not null) activity.SetTag("resourcefull.store", storeName);
    }
}

/// <summary>
/// Registers signed links.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullLinksServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IResourceLinkSigner"/> for a store, and ASP.NET Core Data Protection if it isn't added yet.
    /// Configure Data Protection to keep its keys where every instance of the application can read them, or links made
    /// on one instance won't work on another, nor after a restart.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="storeName">
    /// The store whose links to sign, added with <see cref="ResourcefullServiceCollectionExtensions.AddKeyedResourcefull"/>;
    /// <see langword="null"/>, the default, for the store <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/>
    /// adds. A named store's signer is a keyed service, resolved with <c>[FromKeyedServices(storeName)]</c>, such as
    /// <c>[FromKeyedServices("archive")]</c>. Call this once for each store whose links you serve.
    /// </param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="storeName"/> is empty or white space.</exception>
    public static IServiceCollection AddResourcefullLinks(this IServiceCollection services, string? storeName = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (storeName is not null) ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        services.AddDataProtection();
        if (storeName is null)
        {
            services.TryAddScoped<IResourceLinkSigner, ResourceLinkSigner>();
            return services;
        }

        services.TryAddKeyedScoped<IResourceLinkSigner>(storeName, (provider, _) => new ResourceLinkSigner(
            provider.GetRequiredService<IDataProtectionProvider>(),
            provider.GetKeyedService<IResourceServiceClient>(storeName) ?? throw new InvalidOperationException(
                $"{nameof(AddResourcefullLinks)} needs a store named '{storeName}' added with {nameof(ResourcefullServiceCollectionExtensions.AddKeyedResourcefull)}."),
            provider.GetService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Gets the signer for the store <paramref name="resources"/> works with from the request's services.
    /// <paramref name="caller"/> names the API in the error when that signer isn't added.
    /// </summary>
    /// <exception cref="InvalidOperationException">That store's signed links aren't added.</exception>
    internal static IResourceLinkSigner SignerFor(HttpContext httpContext, IResourceServiceClient resources, string caller)
    {
        string? storeName = resources.StoreName;
        IServiceProvider services = httpContext.RequestServices;
        return (storeName is null ? services.GetService<IResourceLinkSigner>() : services.GetKeyedService<IResourceLinkSigner>(storeName))
            ?? throw new InvalidOperationException(NoSignerMessage(caller, storeName));
    }

    /// <summary>Says that <paramref name="caller"/> needs the signed links of the store named <paramref name="storeName"/> added.</summary>
    internal static string NoSignerMessage(string caller, string? storeName) => storeName is null
        ? $"{caller} needs signed links added with {nameof(AddResourcefullLinks)}."
        : $"{caller} needs signed links for the store '{storeName}' added with {nameof(AddResourcefullLinks)}(\"{storeName}\").";
}
