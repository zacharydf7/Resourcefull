using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Resourcefull.AspNetCore;

/// <summary>
/// What a signed link to a resource lets its holder download, and for how long. Pass it to
/// <see cref="IResourceLinkSigner.CreateTokenAsync"/>.
/// </summary>
/// <param name="ContainerName">The container the resource is in.</param>
/// <param name="ResourceName">The resource to share.</param>
public sealed record ResourceLink(string ContainerName, string ResourceName)
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
    /// Whether browsers should display the file (<c>Content-Disposition: inline</c>) rather than download it. Use it
    /// only for content types your application allows to be shown; see <see cref="ResourceResultOptions.Inline"/>.
    /// </summary>
    public bool Inline { get; init; }
}

/// <summary>What a valid token says its holder may download.</summary>
/// <param name="ContainerName">The container the resource is in.</param>
/// <param name="ResourceName">The resource.</param>
/// <param name="VersionId">The version, or <see langword="null"/> for whichever is current.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
/// <param name="Inline">Whether browsers should display the file rather than download it.</param>
public sealed record SignedResourceLink(string ContainerName, string ResourceName, string? VersionId, DateTimeOffset ExpiresAt, bool Inline);

/// <summary>Whether a token is a link that still works.</summary>
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

/// <summary>The result of checking a token.</summary>
/// <param name="Status">Whether it's a link that still works.</param>
/// <param name="Link">What it grants, for a valid or expired token; <see langword="null"/> for an invalid one.</param>
public sealed record ResourceLinkCheck(ResourceLinkStatus Status, SignedResourceLink? Link);

/// <summary>
/// Creates and checks the tokens of signed links: strings that let whoever holds them download one resource until
/// they expire, without signing in. Register it with <see cref="ResourceLinkServiceCollectionExtensions.AddResourcefullLinks"/>.
/// Tokens are signed and encrypted with ASP.NET Core Data Protection, so they can't be changed, read or made up, and
/// they work on every instance that shares the application's Data Protection keys.
/// </summary>
public interface IResourceLinkSigner
{
    /// <summary>
    /// Creates a token for <paramref name="link"/>, to put into a URL your application serves with
    /// <c>Results.SharedResource</c>. Anyone with the URL can download the resource until the link expires.
    /// </summary>
    /// <param name="link">What to share, and for how long.</param>
    /// <param name="cancellationToken">Cancels looking the resource up.</param>
    /// <returns>The token, or <see langword="null"/> if the resource (or the version asked for) doesn't exist.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ResourceLink.ExpiresIn"/> isn't positive.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="ResourceLink.FollowCurrent"/> is set with a <see cref="ResourceLink.VersionId"/>, or a name breaks the
    /// name rules.
    /// </exception>
    Task<string?> CreateTokenAsync(ResourceLink link, CancellationToken cancellationToken = default);

    /// <summary>Checks a token: whether this application signed it, whether it has expired, and what it grants.</summary>
    /// <param name="token">The token from the link's URL.</param>
    ResourceLinkCheck Check(string token);
}

/// <summary>Signs links with the application's Data Protection keys.</summary>
internal sealed class ResourceLinkSigner(IDataProtectionProvider dataProtection, IResourceServiceClient resources,
    TimeProvider? timeProvider = null) : IResourceLinkSigner
{
    // Changing the purpose would invalidate every link ever made, as removing the keys does.
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Resourcefull.ResourceLink.v1");
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<string?> CreateTokenAsync(ResourceLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(link.ExpiresIn, TimeSpan.Zero, nameof(link));
        if (link.FollowCurrent && link.VersionId is not null)
            throw new ArgumentException("A link can show one version or follow the current one, not both.", nameof(link));

        ResourceInfo? version = await resources.GetResourceContainerClient(link.ContainerName)
            .GetResourceInfoAsync(link.ResourceName, link.VersionId, cancellationToken);
        if (version is null) return null;

        var payload = new Payload(link.ContainerName, link.ResourceName, link.FollowCurrent ? null : version.VersionId,
            (_clock.GetUtcNow() + link.ExpiresIn).ToUnixTimeMilliseconds(), link.Inline);
        return Base64Url.EncodeToString(_protector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload)));
    }

    public ResourceLinkCheck Check(string token)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(Base64Url.DecodeFromChars(token)));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            // Not something this application signed, whatever is wrong with it.
            return new ResourceLinkCheck(ResourceLinkStatus.Invalid, null);
        }

        if (payload is null) return new ResourceLinkCheck(ResourceLinkStatus.Invalid, null);
        var link = new SignedResourceLink(payload.ContainerName, payload.ResourceName, payload.VersionId,
            DateTimeOffset.FromUnixTimeMilliseconds(payload.ExpiresAt), payload.Inline);
        return new ResourceLinkCheck(link.ExpiresAt <= _clock.GetUtcNow() ? ResourceLinkStatus.Expired : ResourceLinkStatus.Valid, link);
    }

    /// <summary>What a token holds, with short names to keep links short.</summary>
    private sealed record Payload(
        [property: JsonPropertyName("c")] string ContainerName,
        [property: JsonPropertyName("n")] string ResourceName,
        [property: JsonPropertyName("v")] string? VersionId,
        [property: JsonPropertyName("e")] long ExpiresAt,
        [property: JsonPropertyName("i")] bool Inline);
}

/// <summary>Registers signed links.</summary>
public static class ResourceLinkServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IResourceLinkSigner"/>, and ASP.NET Core Data Protection if it isn't added yet. Configure Data
    /// Protection to keep its keys where every instance of the application can read them, or links made on one
    /// instance won't work on another, nor after a restart.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddResourcefullLinks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDataProtection();
        services.TryAddScoped<IResourceLinkSigner, ResourceLinkSigner>();
        return services;
    }
}
