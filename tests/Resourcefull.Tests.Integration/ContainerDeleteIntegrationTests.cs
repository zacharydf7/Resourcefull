using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Utilities;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// A container delete runs in batches that each commit, so one canceled partway leaves whole resources behind and
/// finishes when called again. These need a real transaction for each batch, which EF Core's in-memory database
/// doesn't have.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class ContainerDeleteIntegrationTests(SqlServerFixture fixture)
{
    // Two chunks each, against a budget of three chunks, so a batch holds one resource.
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(ResourceChunker.ChunkSize + 1);

    [SkippableFact]
    public async Task DeleteResourceContainerAsync_ShouldLeaveTheRestWholeAndFinishWhenCalledAgain_WhenCanceledAfterABatch()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var (repository, canceledAfterOneBatch, token, containerName) = await NewRepositoriesAsync();
        foreach (string name in new[] { "a", "b", "c" }) await AddAsync(repository, containerName, name, "v1");

        // Act
        Exception? cancellation =
            await Record.ExceptionAsync(() => canceledAfterOneBatch.DeleteResourceContainerAsync(containerName, token));
        List<ResourceInfo> remaining = await repository.GetAllResourceInfoAsync(containerName);
        var remainingContent = new List<byte[]>();
        foreach (ResourceInfo resource in remaining)
        {
            await using Stream content = (await repository.GetResourceStreamAsync(containerName, resource.Name))!;
            var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            remainingContent.Add(copy.ToArray());
        }
        bool finished = await repository.DeleteResourceContainerAsync(containerName);

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(cancellation);
        Assert.Equal(["b", "c"], remaining.Select(r => r.Name).Order());
        Assert.All(remainingContent, content => Assert.Equal(Payload, content));
        Assert.True(finished);
        Assert.Null(await repository.GetResourceContainerInfoAsync(containerName));
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [SkippableFact]
    public async Task DeleteResourceContainerAsync_ShouldKeepTheNameAtItsCurrentVersion_WhenCanceledBetweenVersionsOfOneName()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the current version, "a", was stored last but sorts first.
        var (repository, canceledAfterOneBatch, token, containerName) = await NewRepositoriesAsync();
        await AddAsync(repository, containerName, "n", "b");
        await AddAsync(repository, containerName, "n", "a");

        // Act
        Exception? cancellation =
            await Record.ExceptionAsync(() => canceledAfterOneBatch.DeleteResourceContainerAsync(containerName, token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(cancellation);
        ResourceInfo current = Assert.Single(await repository.GetResourceVersionsAsync(containerName, "n"));
        Assert.Equal("a", current.VersionId);
        Assert.True(current.IsLatest);
        await repository.DeleteResourceContainerAsync(containerName);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(ResourceSqlRepository.ChunksPerDeleteStatement)] // so many that its chunks go before its row
    public async Task DeleteResourceContainerAsync_ShouldLeaveAVersionAMoveTookAway_WhenTheMoveLandsAfterItWasListed(
        int extraChunks)
    {
        fixture.SkipIfUnavailable();

        // Arrange: a version moved to another container, the way a move does it, keeping its ID, as soon as the delete
        // has listed it.
        var (repository, _, _, containerName) = await NewRepositoriesAsync();
        string destination = $"c-{Guid.NewGuid():N}";
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = destination });
        await AddAsync(repository, containerName, "a", "v1");
        await AddChunksAsync(containerName, "a", extraChunks);
        int chunks = await CountChunksAsync(containerName, "a");
        var deleting = new ResourceSqlRepository(new PooledDbContextFactory<ResourceDbContext>(
            new DbContextOptionsBuilder<ResourceDbContext>().UseSqlServer(fixture.ConnectionString)
                .AddInterceptors(new MoveAfterListingInterceptor(fixture, containerName, destination)).Options));

        // Act
        await deleting.DeleteResourceContainerAsync(containerName);

        // Assert
        Assert.Null(await repository.GetResourceContainerInfoAsync(containerName));
        Assert.Equal(["moved"], (await repository.GetAllResourceInfoAsync(destination)).Select(r => r.Name));
        Assert.Equal(chunks, await CountChunksAsync(destination, "moved"));
        await repository.DeleteResourceContainerAsync(destination);
    }

    /// <summary>Gives a version <paramref name="count"/> more one-byte chunks, written set-based.</summary>
    private async Task AddChunksAsync(string containerName, string resourceName, int count)
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data])
            SELECT r.[Id], n.[i] + 100, 0x00
            FROM [Resources] r
            CROSS JOIN (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [i]
                        FROM sys.all_objects a CROSS JOIN sys.all_objects b) n
            WHERE r.[ContainerName] = @container AND r.[Name] = @name
            """;
        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@container", containerName);
        command.Parameters.AddWithValue("@name", resourceName);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> CountChunksAsync(string containerName, string resourceName)
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [ResourceChunks] c JOIN [Resources] r ON r.[Id] = c.[ResourceId] " +
            "WHERE r.[ContainerName] = @container AND r.[Name] = @name";
        command.Parameters.AddWithValue("@container", containerName);
        command.Parameters.AddWithValue("@name", resourceName);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Moves every version in <paramref name="source"/> to <paramref name="destination"/> as "moved", once, as soon as
    /// a delete batch has listed its candidates.
    /// </summary>
    private sealed class MoveAfterListingInterceptor(SqlServerFixture fixture, string source, string destination)
        : DbCommandInterceptor
    {
        private bool _moved;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_moved && command.CommandText.Contains("FROM [Resources]") && command.CommandText.Contains("ORDER BY"))
            {
                _moved = true;
                await using SqlConnection connection = await fixture.OpenConnectionAsync();
                await using SqlCommand move = connection.CreateCommand();
                move.CommandText = "UPDATE [Resources] SET [ContainerName] = @destination, [Name] = 'moved' " +
                    "WHERE [ContainerName] = @source";
                move.Parameters.AddWithValue("@destination", destination);
                move.Parameters.AddWithValue("@source", source);
                await move.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }
    }

    private static Task<WrittenContent> AddAsync(ResourceSqlRepository repository, string containerName, string name,
        string versionId)
        => repository.AddResourceAsync(containerName,
            new ResourceInfo { ContainerName = containerName, Name = name, VersionId = versionId },
            new MemoryStream(Payload));

    /// <summary>
    /// A repository, and one over the same database whose token is canceled as soon as its first transaction commits,
    /// with a budget of three chunks per batch; and a new container.
    /// </summary>
    private async Task<(ResourceSqlRepository Repository, ResourceSqlRepository CanceledAfterOneBatch,
        CancellationToken Token, string ContainerName)> NewRepositoriesAsync()
    {
        var repository = new ResourceSqlRepository(new PooledDbContextFactory<ResourceDbContext>(
            new DbContextOptionsBuilder<ResourceDbContext>().UseSqlServer(fixture.ConnectionString).Options));
        var cancelAfterCommit = new CancelAfterCommitInterceptor();
        var canceledAfterOneBatch = new ResourceSqlRepository(new PooledDbContextFactory<ResourceDbContext>(
            new DbContextOptionsBuilder<ResourceDbContext>().UseSqlServer(fixture.ConnectionString)
                .AddInterceptors(cancelAfterCommit).Options)) { ChunksPerDeleteBatch = 3 };
        string containerName = $"c-{Guid.NewGuid():N}";
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        return (repository, canceledAfterOneBatch, cancelAfterCommit.Token, containerName);
    }

    private sealed class CancelAfterCommitInterceptor : DbTransactionInterceptor
    {
        private readonly CancellationTokenSource _source = new();

        public CancellationToken Token => _source.Token;

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            _source.Cancel();
            return Task.CompletedTask;
        }
    }
}
