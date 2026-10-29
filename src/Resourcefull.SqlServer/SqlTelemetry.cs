using System.Diagnostics;
using System.Reflection;

namespace Resourcefull.SqlServer;

/// <summary>
/// Spans for the SQL Server package's own operations, the backfill and migrations, published under the same source
/// name as the clients' spans and tagged the same way.
/// </summary>
internal static class SqlTelemetry
{
    private static readonly ActivitySource Source = new(ResourcefullTelemetry.ActivitySourceName,
        typeof(SqlTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

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
