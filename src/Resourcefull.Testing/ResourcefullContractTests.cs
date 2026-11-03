using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.AspNetCore")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Integration")]

namespace Resourcefull.Testing;

/// <summary>
/// Behavior every provider must share, driven through the public clients. Each provider's test project derives a
/// class that builds a service provider on it. Every test works in containers of its own, so the tests can share one
/// store.
/// </summary>
public abstract partial class ResourcefullContractTests
{
    /// <summary>
    /// Builds a service provider on the provider under test, applying <paramref name="configure"/> after it, or skips
    /// the test when that provider is unavailable.
    /// </summary>
    protected abstract ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null);

    /// <summary>
    /// The precision the store keeps times to, such as a microsecond for PostgreSQL. A time the tests give, such as an
    /// expiry, is expected back at this precision. One tick, which is exact, by default.
    /// </summary>
    protected virtual TimeSpan TimePrecision => TimeSpan.FromTicks(1);

    /// <summary>
    /// The order the store lists names in: ordinal by default. PostgreSQL's byte order is Unicode code point order,
    /// which differs from ordinal order only between characters beyond U+FFFF and those from U+E000 to U+FFFF.
    /// </summary>
    protected virtual IComparer<string> NameOrder => StringComparer.Ordinal;

    // Switches for the features ResourceRepository leaves optional; the refusal tests are in
    // ResourcefullContractTests.Optional.cs.

    /// <summary>Whether the provider replaces a container's metadata, with <c>SetContainerMetadataAsync</c>.</summary>
    /// <remarks>
    /// Override it to return <see langword="false"/> when the provider doesn't override that member: the feature's
    /// tests are then skipped, and a test checks that the clients refuse it.
    /// </remarks>
    protected virtual bool SupportsContainerMetadata => true;

    /// <summary>
    /// Whether the provider changes a stored version's metadata and content type, with <c>SetResourceMetadataAsync</c>
    /// and <c>SetContentTypeAsync</c>.
    /// </summary>
    /// <remarks>
    /// Override it to return <see langword="false"/> when the provider doesn't override those members: the feature's
    /// tests are then skipped, and a test checks that the clients refuse it.
    /// </remarks>
    protected virtual bool SupportsVersionChanges => true;

    /// <summary>Whether the provider moves and renames resources, with <c>MoveResourceAsync</c>.</summary>
    /// <remarks>
    /// Override it to return <see langword="false"/> when the provider doesn't override that member: the feature's
    /// tests are then skipped, and a test checks that the clients refuse it.
    /// </remarks>
    protected virtual bool SupportsMove => true;

    /// <summary>Whether the provider imports versions as they were exported, with <c>ImportVersionAsync</c>.</summary>
    /// <remarks>
    /// Override it to return <see langword="false"/> when the provider doesn't override that member: the feature's
    /// tests are then skipped, and a test checks that the clients refuse it.
    /// </remarks>
    protected virtual bool SupportsImport => true;

    /// <summary>
    /// Whether the provider supports expiry, with <c>GetExpiredAsync</c>, <c>DeleteExpiredVersionAsync</c> and
    /// <c>SetExpiryAsync</c>.
    /// </summary>
    /// <remarks>
    /// Override it to return <see langword="false"/> when the provider doesn't override those members: the feature's
    /// tests are then skipped, and a test checks that the clients refuse it.
    /// </remarks>
    protected virtual bool SupportsExpiry => true;

    /// <summary>
    /// Whether the provider supports upload sessions, with <c>StartStagedVersionAsync</c>, <c>GetStagedVersionAsync</c>,
    /// <c>AppendStagedContentAsync</c>, <c>GetStagedStreamAsync</c>, <c>CompleteStagedVersionAsync</c> and
    /// <c>DeleteStagedVersionAsync</c>.
    /// </summary>
    /// <remarks>
    /// Override it to return <see langword="false"/> when the provider doesn't override those members: the feature's
    /// tests are then skipped, and a test checks that the clients refuse it.
    /// </remarks>
    protected virtual bool SupportsUploadSessions => true;

    /// <summary>Skips the calling test unless the provider supports <paramref name="feature"/>.</summary>
    private static void Requires(bool supported, string feature) =>
        Assert.SkipUnless(supported, $"The provider doesn't support {feature}.");

    /// <summary>A time the tests give, as the store keeps it: cut to <see cref="TimePrecision"/>.</summary>
    private DateTimeOffset? Kept(DateTimeOffset? time) =>
        time is { } value ? value.AddTicks(-(value.Ticks % TimePrecision.Ticks)) : null;

    private static IResourceServiceClient ServiceOf(ServiceProvider provider) => provider.GetRequiredService<IResourceServiceClient>();

    private static string NewContainerName() => $"c-{Guid.NewGuid():N}";

    private static Task<IResourceContainerClient> NewContainerAsync(ServiceProvider provider) =>
        ServiceOf(provider).CreateContainerAsync(NewContainerName());
}
