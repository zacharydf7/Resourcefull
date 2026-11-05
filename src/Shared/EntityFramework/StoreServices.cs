using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Resourcefull.EntityFramework;

/// <summary>
/// Resolves a store's services, as the core package's own copy does: by its name for a named store, and with the plain
/// calls for the store without one, so that store still works in a container without keyed services.
/// </summary>
internal static class StoreServices
{
    public static T? GetStoreService<T>(this IServiceProvider services, string? storeName) where T : notnull =>
        storeName is null ? services.GetService<T>() : services.GetKeyedService<T>(storeName);

    public static T GetRequiredStoreService<T>(this IServiceProvider services, string? storeName) where T : notnull =>
        storeName is null ? services.GetRequiredService<T>() : services.GetRequiredKeyedService<T>(storeName);

    /// <summary>The metadata keys the store's listings can search for, which its provider indexes.</summary>
    public static IReadOnlySet<string> SearchableMetadataKeys(this IServiceProvider services, string? storeName) =>
        services.GetRequiredService<IOptionsMonitor<ResourcefullOptions>>().Get(storeName ?? Options.DefaultName)
            .SearchableMetadataKeys;
}
