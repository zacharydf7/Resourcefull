using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Resourcefull.ObjectStorage;

/// <summary>
/// The provider package's own count of retried head writes, published under the clients' meter name and tagged as theirs
/// are. Each package compiles its own copy, which takes the provider's name, the counter's name and the version from that
/// package: for example, <c>resourcefull.s3.retries</c> from <c>Resourcefull.S3</c>.
/// </summary>
internal static class ObjectStoreTelemetry
{
    private static readonly AssemblyName Package = typeof(ObjectStoreTelemetry).Assembly.GetName();

    private static readonly string Provider = Package.Name!["Resourcefull.".Length..].ToLowerInvariant();

    private static readonly Meter Meter = new(ResourcefullTelemetry.MeterName,
        typeof(ObjectStoreTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>($"resourcefull.{Provider}.retries", unit: "{retry}",
        description: "Writes to a name's head retried because another writer changed the name first.");

    /// <summary>Counts a head write retried after another writer changed the name first.</summary>
    public static void CountRetry(string? storeName)
    {
        if (!Retries.Enabled) return;
        var tags = new TagList { { "resourcefull.provider", Provider } };
        if (storeName is not null) tags.Add("resourcefull.store", storeName);
        Retries.Add(1, tags);
    }
}
