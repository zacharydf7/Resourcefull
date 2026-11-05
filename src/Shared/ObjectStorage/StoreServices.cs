using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Resourcefull.ObjectStorage;

/// <summary>Reads a store's options for the object-store providers' registrations.</summary>
internal static class StoreServices
{
    /// <summary>The metadata keys the store's listings can search for, which its provider indexes.</summary>
    public static IReadOnlySet<string> SearchableMetadataKeys(this IServiceProvider services, string? storeName) =>
        services.GetRequiredService<IOptionsMonitor<ResourcefullOptions>>().Get(storeName ?? Options.DefaultName)
            .SearchableMetadataKeys;
}
