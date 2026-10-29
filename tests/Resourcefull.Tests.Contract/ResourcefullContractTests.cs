using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

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

    private static IResourceServiceClient ServiceOf(ServiceProvider provider) => provider.GetRequiredService<IResourceServiceClient>();

    private static string NewContainerName() => $"c-{Guid.NewGuid():N}";

    private static Task<IResourceContainerClient> NewContainerAsync(ServiceProvider provider) =>
        ServiceOf(provider).CreateResourceContainerAsync(NewContainerName());
}
