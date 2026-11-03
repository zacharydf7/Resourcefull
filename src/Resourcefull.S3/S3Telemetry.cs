using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Resourcefull.S3;

/// <summary>
/// The S3 package's own count of retried head writes, published under the clients' meter name and tagged as theirs are.
/// </summary>
internal static class S3Telemetry
{
    private const string Provider = "s3";

    private static readonly string? Version =
        typeof(S3Telemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly Meter Meter = new(ResourcefullTelemetry.MeterName, Version);

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("resourcefull.s3.retries", unit: "{retry}",
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
