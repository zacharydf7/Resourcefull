using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Resourcefull.Tests.Performance;

/// <summary>
/// Counts the round trips EF Core makes (commands, and beginning and committing transactions) and the pages SQL Server
/// reads for them, so a change that adds a round trip or a scan shows up whatever the machine's speed. Reads come from
/// <c>SET STATISTICS IO</c>, turned on for every connection as it opens; that statement isn't sent through EF Core, so
/// it isn't counted as a round trip.
/// </summary>
internal sealed partial class SqlActivity : DbCommandInterceptor, IDbConnectionInterceptor, IDbTransactionInterceptor
{
    private long _roundTrips;
    private long _logicalReads;

    public long RoundTrips => Interlocked.Read(ref _roundTrips);
    public long LogicalReads => Interlocked.Read(ref _logicalReads);

    public void Reset()
    {
        Interlocked.Exchange(ref _roundTrips, 0);
        Interlocked.Exchange(ref _logicalReads, 0);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result) => Count(result);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Count(result));

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result) => Count(result);

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(Count(result));

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result) => Count(result);

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default) => ValueTask.FromResult(Count(result));

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        => Count(result);

    public ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData,
        DbTransaction result, CancellationToken cancellationToken = default) => ValueTask.FromResult(Count(result));

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => Count(transaction);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Count(transaction);
        return Task.CompletedTask;
    }

    public async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        var sqlConnection = (SqlConnection)connection;
        sqlConnection.InfoMessage -= OnInfoMessage;
        sqlConnection.InfoMessage += OnInfoMessage;
        await using SqlCommand command = sqlConnection.CreateCommand();
        command.CommandText = "SET STATISTICS IO ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ConnectionOpenedAsync(connection, eventData).GetAwaiter().GetResult();

    private T Count<T>(T result)
    {
        Interlocked.Increment(ref _roundTrips);
        return result;
    }

    // "Table 'ResourceChunks'. Scan count 1, logical reads 5, ..., lob logical reads 640, ..."
    private void OnInfoMessage(object sender, SqlInfoMessageEventArgs e)
    {
        foreach (Match match in LogicalReadsPattern().Matches(e.Message))
        {
            Interlocked.Add(ref _logicalReads, long.Parse(match.Groups[1].ValueSpan));
        }
    }

    [GeneratedRegex(@"logical reads (\d+)")]
    private static partial Regex LogicalReadsPattern();
}
