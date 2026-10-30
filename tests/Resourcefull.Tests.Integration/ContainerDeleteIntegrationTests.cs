using System.Data.Common;
using System.Security.Cryptography;
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
