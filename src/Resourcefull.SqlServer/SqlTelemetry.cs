using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Resourcefull.SqlServer;

/// <summary>
/// Spans for the SQL Server package's own operations, the backfill, migrations and health check, and its count of
/// retries, published under the same source and meter names as the clients' and tagged the same way.
/// </summary>
internal static class SqlTelemetry
{
    private static readonly string? Version =
        typeof(SqlTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private static readonly ActivitySource Source = new(ResourcefullTelemetry.ActivitySourceName, Version);

    private static readonly Meter Meter = new(ResourcefullTelemetry.MeterName, Version);

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("resourcefull.sqlserver.retries", unit: "{retry}",
        description: "Database calls retried after a transient failure, such as a deadlock or a dropped connection.");

    /// <summary>Counts a retry, tagged with the failure's type and, for a SQL Server error, its number.</summary>
    public static void CountRetry(Exception exception)
    {
        if (!Retries.Enabled) return;
        var tags = new TagList { { "resourcefull.provider", "sqlserver" }, { "error.type", exception.GetType().FullName } };
        if ((exception as SqlException ?? exception.InnerException as SqlException) is { } sql)
            tags.Add("db.response.status_code", sql.Number.ToString(CultureInfo.InvariantCulture));
        Retries.Add(1, tags);
    }

    public static Activity? Start(string operation)
    {
        Activity? activity = Source.StartActivity($"resourcefull {operation}");
        activity?.SetTag("resourcefull.operation", operation);
        activity?.SetTag("resourcefull.provider", "sqlserver");
        return activity;
    }

    /// <summary>
    /// Records a failure on the span, message included: the backfill's and migrations' failures don't name resources.
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
