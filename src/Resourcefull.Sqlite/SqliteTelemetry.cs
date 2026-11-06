using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Resourcefull.Sqlite;

/// <summary>
/// Spans for the SQLite package's own operations, migrations and the health check, and its count of abandoned uploads,
/// published under the same source and meter names as the clients' and tagged the same way.
/// </summary>
internal static class SqliteTelemetry
{
    private const string Provider = "sqlite";

    private static readonly string? Version =
        typeof(SqliteTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly ActivitySource Source = new(ResourcefullTelemetry.ActivitySourceName, Version);

    private static readonly Meter Meter = new(ResourcefullTelemetry.MeterName, Version);

    private static readonly Counter<long> AbandonedUploads = Meter.CreateCounter<long>(
        "resourcefull.sqlite.abandoned_uploads", unit: "{upload}",
        description: "Staged versions of uploads that stopped before finishing, deleted by the expiry sweep.");

    /// <summary>Counts the staged versions of abandoned uploads a sweep deleted.</summary>
    public static void CountAbandonedUploads(long deleted, string? storeName)
    {
        if (deleted > 0) AbandonedUploads.Add(deleted, StoreTags(storeName));
    }

    public static Activity? Start(string operation, string? storeName)
    {
        Activity? activity = Source.StartActivity($"resourcefull {operation}");
        activity?.SetTag("resourcefull.operation", operation);
        activity?.SetTag("resourcefull.provider", Provider);
        if (storeName is not null) activity?.SetTag("resourcefull.store", storeName);
        return activity;
    }

    /// <summary>The tags every measurement carries, as the clients' do: the provider, and the store's name if it has one.</summary>
    private static TagList StoreTags(string? storeName)
    {
        var tags = new TagList { { "resourcefull.provider", Provider } };
        if (storeName is not null) tags.Add("resourcefull.store", storeName);
        return tags;
    }

    /// <summary>
    /// Records a failure on the span, message included: migrations' and health checks' failures don't name resources.
    /// Returns false, so it can sit in an exception filter that rethrows nothing.
    /// </summary>
    public static bool Failed(Activity? activity, Exception exception)
    {
        if (activity is not null)
        {
            activity.SetTag("error.type", exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddException(exception);
        }
        return false;
    }
}
