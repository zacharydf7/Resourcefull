using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Resourcefull.Tests.Performance;

/// <summary>
/// Counts the round trips made to SQL Server (commands, savepoints, and beginning and committing transactions) and the
/// pages it reads for them, so a change that adds a round trip or a scan shows up whatever the machine's speed.
/// Commands are counted from SqlClient's diagnostics, so the ones the provider sends without EF Core count too;
/// transactions and savepoints come from EF Core. Reads come from <c>SET STATISTICS IO</c>, turned on for every
/// connection as it opens, and that statement isn't counted.
/// </summary>
internal sealed partial class SqlActivity : IDbConnectionInterceptor, IDbTransactionInterceptor,
    IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private const string StatisticsCommand = "SET STATISTICS IO ON;";

    private readonly List<IDisposable> _subscriptions = [];
    private long _roundTrips;
    private long _logicalReads;

    public SqlActivity() => _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

    public long RoundTrips => Interlocked.Read(ref _roundTrips);
    public long LogicalReads => Interlocked.Read(ref _logicalReads);

    public void Reset()
    {
        Interlocked.Exchange(ref _roundTrips, 0);
        Interlocked.Exchange(ref _logicalReads, 0);
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "SqlClientDiagnosticListener")
        {
            lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this));
        }
    }

    public void OnNext(KeyValuePair<string, object?> diagnostic)
    {
        if (diagnostic.Value is SqlClientCommandBefore { Command.CommandText: not StatisticsCommand }) Count(diagnostic);
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void Dispose()
    {
        lock (_subscriptions)
        {
            foreach (IDisposable subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }

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

    // EF Core sets a savepoint before each SaveChanges inside a transaction the caller began.
    public void CreatedSavepoint(DbTransaction transaction, TransactionEventData eventData) => Count(transaction);

    public Task CreatedSavepointAsync(DbTransaction transaction, TransactionEventData eventData,
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
        command.CommandText = StatisticsCommand;
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
