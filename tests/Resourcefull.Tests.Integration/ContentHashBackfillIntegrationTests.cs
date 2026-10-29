using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Versions stored before hashes were kept get one from the backfill. Each test uses a database of its own, so the
/// backfill only ever sees the versions the test wrote and then stripped of their hash.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class ContentHashBackfillIntegrationTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task RunAsync_ShouldRecordEveryMissingHashAndReportWrongLengthsOnEveryRun_WhenRunTwice()
    {
        fixture.SkipIfUnavailable();

        // Arrange: three versions without a hash, one of them with a length recorded wrong.
        (ServiceProvider provider, string connectionString) = await NewDatabaseAsync("Backfill");
        using (provider)
        {
            IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
            ResourceInfo first = await container.UploadAsync("a", BinaryData.FromString("first"));
            ResourceInfo second = await container.UploadAsync("b", new MemoryStream(new byte[200_000]));
            ResourceInfo third = await container.UploadAsync("c", BinaryData.FromString(""));
            await ExecuteAsync(connectionString, "UPDATE [Resources] SET [ContentHash] = NULL");
            await ExecuteAsync(connectionString, $"UPDATE [Resources] SET [ContentLength] = 999 WHERE [VersionId] = '{second.VersionId}'");
            using IServiceScope scope = provider.CreateScope();
            var backfill = scope.ServiceProvider.GetRequiredService<ContentHashBackfill>();

            // Act
            ContentHashBackfillResult firstRun = await backfill.RunAsync();
            ContentHashBackfillResult secondRun = await backfill.RunAsync();

            // Assert
            Assert.Equal(3, firstRun.Hashed);
            ContentLengthMismatch mismatch = Assert.Single(firstRun.Mismatches);
            Assert.Equal(new ContentLengthMismatch("c", "b", second.VersionId!, 999, 200_000), mismatch);
            Assert.Equal(0, secondRun.Hashed);
            Assert.Equal(mismatch, Assert.Single(secondRun.Mismatches));
            Assert.Equal(first.ContentHash, (await container.GetResourceInfoAsync("a"))!.ContentHash);
            Assert.Equal(second.ContentHash, (await container.GetResourceInfoAsync("b"))!.ContentHash);
            Assert.Equal(third.ContentHash, (await container.GetResourceInfoAsync("c"))!.ContentHash);
        }
    }

    [SkippableFact]
    public async Task RunAsync_ShouldKeepWhatItRecordedAndFinishWhenRunAgain_WhenStoppedPartway()
    {
        fixture.SkipIfUnavailable();

        // Arrange: three versions without a hash, the first stored with a length recorded wrong, and a run cancelled as
        // soon as it records its first hash, before it could report anything.
        (ServiceProvider provider, string connectionString) = await NewDatabaseAsync("BackfillResume");
        using (provider)
        {
            IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
            foreach (string name in new[] { "a", "b", "c" })
            {
                await container.UploadAsync(name, BinaryData.FromString(name));
            }
            await ExecuteAsync(connectionString, "UPDATE [Resources] SET [ContentHash] = NULL");
            await ExecuteAsync(connectionString, "UPDATE [Resources] SET [ContentLength] = 999 WHERE [Name] = 'a'");
            using var cts = new CancellationTokenSource();
            DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(new CancelAfterUpdateInterceptor(cts))
                .Options;
            var stoppedAfterOne = new ContentHashBackfill(new PooledDbContextFactory<ResourceDbContext>(options));
            using IServiceScope scope = provider.CreateScope();
            var backfill = scope.ServiceProvider.GetRequiredService<ContentHashBackfill>();

            // Act
            Exception? stopped = await Record.ExceptionAsync(() => stoppedAfterOne.RunAsync(cts.Token));
            int recordedBeforeTheStop = (int)(await ScalarAsync(connectionString, "SELECT COUNT(*) FROM [Resources] WHERE [ContentHash] IS NOT NULL"))!;
            ContentHashBackfillResult resumed = await backfill.RunAsync();

            // Assert
            Assert.IsAssignableFrom<OperationCanceledException>(stopped);
            Assert.Equal(1, recordedBeforeTheStop);
            Assert.Equal(2, resumed.Hashed);
            Assert.Equal("a", Assert.Single(resumed.Mismatches).Name);
            Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM [Resources] WHERE [ContentHash] IS NULL"));
        }
    }

    [SkippableFact]
    public async Task RunAsync_ShouldSkipAVersionAndFinish_WhenTheVersionIsDeletedAfterTheRunFindsIt()
    {
        fixture.SkipIfUnavailable();

        // Arrange: three versions without a hash; "b" is deleted as soon as the run has listed them.
        (ServiceProvider provider, string connectionString) = await NewDatabaseAsync("BackfillDeleted");
        using (provider)
        {
            IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync("c");
            foreach (string name in new[] { "a", "b", "c" })
            {
                await container.UploadAsync(name, BinaryData.FromString(name));
            }
            await ExecuteAsync(connectionString, "UPDATE [Resources] SET [ContentHash] = NULL");
            DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
                .UseSqlServer(connectionString)
                .AddInterceptors(new DeleteAfterListingInterceptor(connectionString, "b"))
                .Options;
            var backfill = new ContentHashBackfill(new PooledDbContextFactory<ResourceDbContext>(options));

            // Act
            ContentHashBackfillResult result = await backfill.RunAsync();

            // Assert
            Assert.Equal(2, result.Hashed);
            Assert.Empty(result.Mismatches);
            Assert.Null(await container.GetResourceInfoAsync("b"));
            Assert.NotNull((await container.GetResourceInfoAsync("a"))!.ContentHash);
            Assert.NotNull((await container.GetResourceInfoAsync("c"))!.ContentHash);
        }
    }

    private async Task<(ServiceProvider Provider, string ConnectionString)> NewDatabaseAsync(string suffix)
    {
        string connectionString = fixture.AllocateDatabase(suffix);
        await using (ResourceDbContext context = SqlServerFixture.CreateDbContext(connectionString))
        {
            await context.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseSqlServer(connectionString));
        return (services.BuildServiceProvider(), connectionString);
    }

    private static Task ExecuteAsync(string connectionString, string sql) => ScalarAsync(connectionString, sql);

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Deletes a version once the backfill has listed the versions it will hash.</summary>
    private sealed class DeleteAfterListingInterceptor(string connectionString, string name) : DbCommandInterceptor
    {
        private bool _deleted;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_deleted && command.CommandText.Contains("[ContentHash] IS NULL", StringComparison.Ordinal))
            {
                _deleted = true;
                await ExecuteAsync(connectionString, $"DELETE FROM [Resources] WHERE [Name] = '{name}'");
            }
            return result;
        }
    }

    /// <summary>Cancels the run once the backfill has recorded a hash.</summary>
    private sealed class CancelAfterUpdateInterceptor(CancellationTokenSource source) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)) source.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
