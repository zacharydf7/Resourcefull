using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Resourcefull.SqlServer;

/// <summary>
/// SQL Server's retrying execution strategy, counting each retry in <c>resourcefull.sqlserver.retries</c>, so a storm
/// of transient failures shows up in metrics rather than only as slower operations.
/// </summary>
internal sealed class RetryCountingExecutionStrategy(ExecutionStrategyDependencies dependencies, int maxRetryCount,
    TimeSpan maxRetryDelay, ICollection<int> errorNumbersToAdd)
    : SqlServerRetryingExecutionStrategy(dependencies, maxRetryCount, maxRetryDelay, errorNumbersToAdd)
{
    protected override void OnRetry()
    {
        SqlTelemetry.CountRetry(ExceptionsEncountered[^1]);
        base.OnRetry();
    }
}
