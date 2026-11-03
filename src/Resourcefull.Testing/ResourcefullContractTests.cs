using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>A time the tests give, as the store keeps it: cut to <see cref="TimePrecision"/>.</summary>
    private DateTimeOffset? Kept(DateTimeOffset? time) =>
        time is { } value ? value.AddTicks(-(value.Ticks % TimePrecision.Ticks)) : null;

    private static IResourceServiceClient ServiceOf(ServiceProvider provider) => provider.GetRequiredService<IResourceServiceClient>();

    private static string NewContainerName() => $"c-{Guid.NewGuid():N}";

    private static Task<IResourceContainerClient> NewContainerAsync(ServiceProvider provider) =>
        ServiceOf(provider).CreateContainerAsync(NewContainerName());
}
