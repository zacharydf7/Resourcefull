using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The retry policy that <c>UseSqlServer</c> configures, against a real deadlock: SQL Server picks the low-priority
/// session as the victim (error 1205), and the policy must run that work again rather than fail it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class RetryPolicyIntegrationTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task UseSqlServer_ShouldRetryTheWork_WhenItIsChosenAsADeadlockVictim()
    {
        fixture.SkipIfUnavailable();

        // Arrange: two tables that the two sessions lock in opposite orders.
        string tableA = $"DeadlockA_{Guid.NewGuid():N}", tableB = $"DeadlockB_{Guid.NewGuid():N}";
        await using (SqlConnection setup = await fixture.OpenConnectionAsync())
        await using (SqlCommand create = setup.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {tableA} (V int); INSERT {tableA} VALUES (0); CREATE TABLE {tableB} (V int); INSERT {tableB} VALUES (0);";
            await create.ExecuteNonQueryAsync();
        }

        // Unpooled, so neither session's deadlock priority outlives this test on a pooled connection.
        string connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString;
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(connectionString));
        using ServiceProvider provider = services.BuildServiceProvider();
        await using ResourceDbContext dbContext = await provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>().CreateDbContextAsync();
        var bLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;

        // The opponent locks B, then waits for A; it commits once the deadlock is resolved in its favor.
        Task opponent = Task.Run(async () =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await ExecuteAsync(connection, transaction, $"SET DEADLOCK_PRIORITY HIGH; UPDATE {tableB} SET V = 1;");
            bLocked.SetResult();
            await aLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await ExecuteAsync(connection, transaction, $"UPDATE {tableA} SET V = 1;");
            await transaction.CommitAsync();
        });

        // Act: the work locks A, then waits for B, closing the cycle; as the low-priority session it is the victim.
        string updateA = $"SET DEADLOCK_PRIORITY LOW; UPDATE {tableA} SET V = 2;", updateB = $"UPDATE {tableB} SET V = 2;";
        await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            attempts++;
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            await dbContext.Database.ExecuteSqlRawAsync(updateA);
            await bLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
            aLocked.TrySetResult();
            await dbContext.Database.ExecuteSqlRawAsync(updateB);
            await transaction.CommitAsync();
        });
        await opponent;

        // Assert: the first attempt was the victim, and the retry stored the work after the opponent's.
        Assert.Equal(2, attempts);
        await using SqlConnection check = await fixture.OpenConnectionAsync();
        await using SqlCommand read = check.CreateCommand();
        read.CommandText = $"SELECT (SELECT V FROM {tableA}) * 10 + (SELECT V FROM {tableB});";
        Assert.Equal(22, (int)(await read.ExecuteScalarAsync())!);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
