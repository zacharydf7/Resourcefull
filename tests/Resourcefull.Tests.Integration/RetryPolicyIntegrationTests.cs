using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The retry policy that <c>UseSqlServer</c> configures, against a real deadlock: SQL Server picks the low-priority
/// session as the victim (error 1205), and the policy must run that work again, counting the retry, rather than fail it.
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
        // Other tests may retry at the same time, so only this deadlock's error number is looked for.
        var retries = new ConcurrentQueue<string?>();
        using MeterListener listener = ListenForRetries(retries);

        // The opponent locks B, then waits for A; it commits once the deadlock is resolved in its favor.
        Task opponent = Task.Run(async () =>
        {
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
                await ExecuteAsync(connection, transaction, $"SET DEADLOCK_PRIORITY HIGH; UPDATE {tableB} SET V = 1;");
                bLocked.SetResult();
                await aLocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await ExecuteAsync(connection, transaction, $"UPDATE {tableA} SET V = 1;");
                await transaction.CommitAsync();
            }
            catch (Exception exception)
            {
                // Fails the work's wait for B at once, with the real cause inside an exception the policy never
                // retries.
                bLocked.TrySetException(
                    new InvalidOperationException("The opponent session failed before locking B.", exception));
                throw;
            }
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
        Assert.Contains("1205", retries);
        await using SqlConnection check = await fixture.OpenConnectionAsync();
        await using SqlCommand read = check.CreateCommand();
        read.CommandText = $"SELECT (SELECT V FROM {tableA}) * 10 + (SELECT V FROM {tableB});";
        Assert.Equal(22, (int)(await read.ExecuteScalarAsync())!);
    }

    [SkippableFact]
    public async Task DeleteExpiredVersionsAsync_ShouldRetryTheDeletes_WhenANameLockIsChosenAsADeadlockVictim()
    {
        fixture.SkipIfUnavailable();

        // Arrange: two expired names, whose locks the delete takes in one statement, "a" first; the opponent holds "b"'s.
        string connectionString = new SqlConnectionStringBuilder(fixture.ConnectionString) { Pooling = false }.ConnectionString;
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(connectionString));
        using ServiceProvider provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<IResourceServiceClient>();
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await service.CreateResourceContainerAsync(containerName);
        var expired = new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) };
        ResourceInfo a = await container.UploadAsync("a", BinaryData.FromString("a"), expired);
        ResourceInfo b = await container.UploadAsync("b", BinaryData.FromString("b"), expired);
        var retries = new ConcurrentQueue<string?>();
        using MeterListener listener = ListenForRetries(retries);
        await using var opponent = new SqlConnection(connectionString);
        await opponent.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await opponent.BeginTransactionAsync();
        // On its own: a command with parameters runs through sp_executesql, which undoes its SET when it ends.
        await ExecuteAsync(opponent, transaction, "SET DEADLOCK_PRIORITY HIGH;");
        await LockNameAsync(opponent, transaction, new ResourceLocation(containerName, "b"));

        // Act: once the delete holds "a" and waits for "b", the opponent asks for "a", closing the cycle.
        Task<IReadOnlyCollection<(ResourceLocation, string)>> delete = provider.GetRequiredService<IResourceRepository>()
            .DeleteExpiredVersionsAsync([(new ResourceLocation(containerName, "a"), a.VersionId),
                (new ResourceLocation(containerName, "b"), b.VersionId)], DateTimeOffset.UtcNow);
        await using (SqlCommand waiting = opponent.CreateCommand())
        {
            waiting.Transaction = transaction;
            waiting.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE [blocking_session_id] = @@SPID";
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((int)(await waiting.ExecuteScalarAsync())! == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "The delete never waited for the second name's lock.");
                await Task.Delay(50);
            }
        }
        await LockNameAsync(opponent, transaction, new ResourceLocation(containerName, "a"));
        await transaction.CommitAsync();
        IReadOnlyCollection<(ResourceLocation, string)> deleted = await delete;

        // Assert: the delete's lock lost the deadlock, and the retry deleted both.
        Assert.Contains(ResourceSqlRepository.NameLockNotGrantedError.ToString(), retries);
        Assert.Equal(2, deleted.Count);
    }

    private static async Task LockNameAsync(SqlConnection connection, SqlTransaction transaction, ResourceLocation name)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "EXEC sp_getapplock @Resource = @lock, @LockMode = 'Exclusive', @LockOwner = 'Transaction';";
        command.Parameters.AddWithValue("@lock", ResourceSqlRepository.NameLockResource(name));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Adds the error number of each retry counted in <c>resourcefull.sqlserver.retries</c> to <paramref name="retries"/>.</summary>
    private static MeterListener ListenForRetries(ConcurrentQueue<string?> retries)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.sqlserver.retries") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Key == "db.response.status_code") retries.Enqueue(tag.Value as string);
        });
        listener.Start();
        return listener;
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
