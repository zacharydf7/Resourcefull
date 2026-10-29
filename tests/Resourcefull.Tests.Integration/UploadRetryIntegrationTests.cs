using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// An upload the execution strategy retries after a transient failure. Retries need a real transaction to roll back,
/// so these run against SQL Server, with one simulated failure injected at a chosen point of the write.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class UploadRetryIntegrationTests(SqlServerFixture fixture)
{
    // 32 chunks, so the content goes in over several batches.
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(65_535 * 32);
    private static readonly string PayloadHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Payload));

    [SkippableFact]
    public async Task AddResourceAsync_ShouldReadTheContentAgainFromWhereItStartedAndStoreIt_WhenASeekableUploadFailsPartway()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the third save is the second batch of chunks, after some content has been written; the content
        // starts past a header the caller has already read.
        var failure = new FailOnceInterceptor(failAtSave: 3);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure);
        var content = new MemoryStream([.. "header"u8, .. Payload]) { Position = "header"u8.Length };

        // Act
        WrittenContent written = await repository.AddResourceAsync(c, Version(c), content);

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(PayloadHash, written.ContentHash);
        Assert.Equal(Payload.Length, written.ContentLength);
        Assert.Equal(Payload, await ReadAsync(repository, c));
        Assert.Single(await repository.GetResourceVersionsAsync(c, "n"));
    }

    [SkippableFact]
    public async Task AddResourceAsync_ShouldRefuseToRetryAndStoreNothing_WhenANonSeekableUploadFailsPartway()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var failure = new FailOnceInterceptor(failAtSave: 3);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure);

        // Act
        Task act() => repository.AddResourceAsync(c, Version(c), new NonSeekableStream(Payload));

        // Assert
        InvalidOperationException refusal = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.IsType<SimulatedTransientException>(refusal.InnerException);
        Assert.Empty(await repository.GetResourceVersionsAsync(c, "n"));
    }

    [SkippableFact]
    public async Task AddResourceAsync_ShouldReturnWhatWasStoredWithoutStoringItAgain_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: create-only too, which must not conflict with its own stored version.
        var failure = new FailOnceInterceptor(failAfterCommit: true);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure);

        // Act
        WrittenContent written = await repository.AddResourceAsync(c, Version(c), new NonSeekableStream(Payload), createOnly: true);

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(PayloadHash, written.ContentHash);
        ResourceInfo stored = Assert.Single(await repository.GetResourceVersionsAsync(c, "n"));
        Assert.True(stored.IsLatest);
        Assert.Equal(Payload, await ReadAsync(repository, c));
    }

    [SkippableFact]
    public async Task AddResourceAsync_ShouldRetryAndStoreTheContent_WhenANonSeekableUploadFailsBeforeAnyContentIsRead()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the first save inserts the version row, before the content is read.
        var failure = new FailOnceInterceptor(failAtSave: 1);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure);

        // Act
        WrittenContent written = await repository.AddResourceAsync(c, Version(c), new NonSeekableStream(Payload));

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(PayloadHash, written.ContentHash);
        Assert.Single(await repository.GetResourceVersionsAsync(c, "n"));
        Assert.Equal(Payload, await ReadAsync(repository, c));
    }

    private async Task<(ResourceSqlRepository, string)> NewRepositoryAsync(FailOnceInterceptor failure)
    {
        DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
            .UseSqlServer(fixture.ConnectionString, sql => sql.ExecutionStrategy(dependencies => new RetryOnSimulatedFailure(dependencies)))
            .AddInterceptors(failure)
            .Options;
        var repository = new ResourceSqlRepository(new PooledDbContextFactory<ResourceDbContext>(options));
        string containerName = $"c-{Guid.NewGuid():N}";
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        failure.Arm();
        return (repository, containerName);
    }

    private static ResourceInfo Version(string containerName) => new() { ContainerName = containerName, Name = "n", VersionId = "v1" };

    private static async Task<byte[]> ReadAsync(ResourceSqlRepository repository, string containerName)
    {
        await using Stream stream = (await repository.GetResourceStreamAsync(containerName, "n"))!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private sealed class SimulatedTransientException() : Exception("A simulated transient failure.");

    private sealed class RetryOnSimulatedFailure(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SimulatedTransientException;
    }

    /// <summary>Throws once, either on the given save of the upload or just after its transaction commits.</summary>
    private sealed class FailOnceInterceptor(int failAtSave = 0, bool failAfterCommit = false) : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        private bool _armed;
        private int _saves;

        public bool Failed { get; private set; }

        public void Arm() => _armed = true;

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed && !Failed && ++_saves == failAtSave) Fail();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (_armed && !Failed && failAfterCommit) Fail();
            return Task.CompletedTask;
        }

        private void Fail()
        {
            Failed = true;
            throw new SimulatedTransientException();
        }
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
