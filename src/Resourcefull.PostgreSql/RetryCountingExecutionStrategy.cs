using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Resourcefull.PostgreSql;

/// <summary>
/// PostgreSQL's retrying execution strategy, counting each retry in <c>resourcefull.postgresql.retries</c>, so a storm
/// of transient failures shows up in metrics rather than only as slower operations.
/// </summary>
internal sealed class RetryCountingExecutionStrategy(ExecutionStrategyDependencies dependencies, int maxRetryCount,
    TimeSpan maxRetryDelay, ICollection<string> errorCodesToAdd, string? storeName = null)
    : NpgsqlRetryingExecutionStrategy(dependencies, maxRetryCount, maxRetryDelay, errorCodesToAdd)
{
    protected override void OnRetry()
    {
        PostgreSqlTelemetry.CountRetry(ExceptionsEncountered[^1], storeName);
        base.OnRetry();
    }
}
