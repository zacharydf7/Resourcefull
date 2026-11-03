using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Resourcefull.ObjectStorage;

/// <summary>
/// The provider package's own counts of retried head writes and dropped expiry entries, published under the clients'
/// meter name and tagged as theirs are. Each package compiles its own copy, which takes the provider's name, each
/// counter's name and the version from that package: for example, <c>resourcefull.s3.retries</c> from
/// <c>Resourcefull.S3</c>.
/// </summary>
internal static class ObjectStoreTelemetry
{
    private static readonly AssemblyName Package = typeof(ObjectStoreTelemetry).Assembly.GetName();

    private static readonly string Provider = Package.Name!["Resourcefull.".Length..].ToLowerInvariant();

    private static readonly Meter Meter = new(ResourcefullTelemetry.MeterName,
        typeof(ObjectStoreTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>($"resourcefull.{Provider}.retries", unit: "{retry}",
        description: "Writes to a name's head retried because another writer changed the name first.");

    private static readonly Counter<long> StaleExpiries = Meter.CreateCounter<long>($"resourcefull.{Provider}.stale_expiries",
        unit: "{entry}", description: "Expiry entries dropped because their version no longer existed or had another expiry.");

    /// <summary>Counts a head write retried after another writer changed the name first.</summary>
    public static void CountRetry(string? storeName) => Count(Retries, storeName);

    /// <summary>Counts an expiry entry dropped because its version no longer existed or had another expiry.</summary>
    public static void CountStaleExpiry(string? storeName) => Count(StaleExpiries, storeName);

    private static void Count(Counter<long> counter, string? storeName)
    {
        if (!counter.Enabled) return;
        var tags = new TagList { { "resourcefull.provider", Provider } };
        if (storeName is not null) tags.Add("resourcefull.store", storeName);
        counter.Add(1, tags);
    }
}
