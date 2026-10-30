using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// A write or download the execution strategy retries after a transient failure. An upload's retry needs a real
/// transaction to roll back, and a download's a real connection to read pages through, so these run against SQL Server,
/// with one simulated failure injected at a chosen point.
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

    [SkippableFact]
    public async Task DeleteResourceAsync_ShouldReportTheDelete_WhenAConditionalDeleteCommitsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the retry finds the version it matched gone, so its conditions no longer hold.
        var failure = new FailOnceInterceptor(failAfterCommit: true);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure, arm: false);
        await repository.AddResourceAsync(c, Version(c), new MemoryStream(Payload));
        failure.Arm();

        // Act
        bool deleted = await repository.DeleteResourceAsync(c, "n", null, new ResourceConditions { IfMatchVersionId = "v1" });

        // Assert
        Assert.True(failure.Failed);
        Assert.True(deleted);
        Assert.Empty(await repository.GetResourceVersionsAsync(c, "n"));
    }

    [SkippableFact]
    public async Task AddResourceContainerAsync_ShouldCreateTheContainer_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var failure = new FailOnceInterceptor(failAfterCommit: true);
        (ResourceSqlRepository repository, _) = await NewRepositoryAsync(failure);
        string created = $"c-{Guid.NewGuid():N}";

        // Act
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = created });

        // Assert
        Assert.True(failure.Failed);
        Assert.NotNull(await repository.GetResourceContainerInfoAsync(created));
    }

    [SkippableFact]
    public async Task DeleteResourceContainerAsync_ShouldReportTheDelete_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the container is empty, so the first commit is the pass that finds no resources to delete.
        var failure = new FailOnceInterceptor(failAfterCommit: true, commitsBeforeFailing: 1);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure);

        // Act
        bool deleted = await repository.DeleteResourceContainerAsync(c);

        // Assert
        Assert.True(failure.Failed);
        Assert.True(deleted);
        Assert.Null(await repository.GetResourceContainerInfoAsync(c));
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldReturnWhatItStoredWithoutCopyingAgain_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var failure = new FailOnceInterceptor(failAfterCommit: true);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure, arm: false);
        await repository.AddResourceAsync(c, Version(c), new MemoryStream(Payload));
        failure.Arm();
        var copy = new ResourceInfo { ContainerName = c, Name = "copy", VersionId = "v2" };

        // Act
        WrittenContent? written = await repository.CopyResourceAsync(new ResourceLocation(c, "n"), "v1", copy, new ResourceWriteCondition());

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(Payload.Length, written!.ContentLength);
        Assert.Equal(["v2"], (await repository.GetResourceVersionsAsync(c, "copy")).Select(v => v.VersionId));
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldRetryAndStoreTheCopy_WhenACopyToItsOwnNameFailsPartway()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a restore copies an older version to its own name; the copy fails on its second save.
        var failure = new FailOnceInterceptor(failAtSave: 2);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure, arm: false);
        await repository.AddResourceAsync(c, Version(c), new MemoryStream(Payload));
        failure.Arm();
        var restored = new ResourceInfo { ContainerName = c, Name = "n", VersionId = "v2", CopiedFrom = new ResourceOrigin(c, "n", "v1") };

        // Act
        WrittenContent? written = await repository.CopyResourceAsync(new ResourceLocation(c, "n"), "v1", restored, new ResourceWriteCondition());

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(Payload.Length, written!.ContentLength);
        Assert.Equal(["v2", "v1"], (await repository.GetResourceVersionsAsync(c, "n")).Select(v => v.VersionId));
        Assert.Equal(Payload, await ReadAsync(repository, c));
    }

    [SkippableFact]
    public async Task MoveResourceAsync_ShouldReportTheMove_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var failure = new FailOnceInterceptor(failAfterCommit: true);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure, arm: false);
        await repository.AddResourceAsync(c, Version(c), new MemoryStream(Payload));
        failure.Arm();

        // Act
        bool moved = await repository.MoveResourceAsync(new ResourceLocation(c, "n"), new ResourceLocation(c, "moved"), null);

        // Assert
        Assert.True(failure.Failed);
        Assert.True(moved);
        Assert.Empty(await repository.GetResourceVersionsAsync(c, "n"));
        Assert.Equal(["v1"], (await repository.GetResourceVersionsAsync(c, "moved")).Select(v => v.VersionId));
    }

    [SkippableFact]
    public async Task SetExpiryAsync_ShouldReportTheChange_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var failure = new FailOnceInterceptor(failAfterCommit: true);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure, arm: false);
        await repository.AddResourceAsync(c, Version(c), new MemoryStream(Payload));
        failure.Arm();
        var expiresAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Act
        bool changed = await repository.SetExpiryAsync(new ResourceLocation(c, "n"), "v1", expiresAt);

        // Assert
        Assert.True(failure.Failed);
        Assert.True(changed);
        Assert.Equal(expiresAt, (await repository.GetResourceInfoAsync(c, "n", "v1"))!.ExpiresAt);
    }

    [SkippableFact]
    public async Task DeleteExpiredVersionAsync_ShouldReportTheDelete_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var failure = new FailOnceInterceptor(failAfterCommit: true);
        (ResourceSqlRepository repository, string c) = await NewRepositoryAsync(failure, arm: false);
        ResourceInfo version = Version(c);
        version.ExpiresAt = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await repository.AddResourceAsync(c, version, new MemoryStream(Payload));
        failure.Arm();

        // Act
        bool deleted = await repository.DeleteExpiredVersionAsync(new ResourceLocation(c, "n"), "v1", DateTimeOffset.UtcNow);

        // Assert
        Assert.True(failure.Failed);
        Assert.True(deleted);
        Assert.Empty(await repository.GetResourceVersionsAsync(c, "n"));
    }

    private async Task<(ResourceSqlRepository, string)> NewRepositoryAsync(FailOnceInterceptor failure, bool arm = true)
    {
        DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
            .UseSqlServer(fixture.ConnectionString, sql => sql.ExecutionStrategy(dependencies => new RetryOnSimulatedFailure(dependencies)))
            .AddInterceptors(failure)
            .Options;
        var repository = new ResourceSqlRepository(new PooledDbContextFactory<ResourceDbContext>(options));
        string containerName = $"c-{Guid.NewGuid():N}";
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        if (arm) failure.Arm();
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

    [SkippableFact]
    public async Task GetResourceStreamAsync_ShouldReadThePageAgainAndReturnTheExactContent_WhenReadingAPageFailsOnce()
    {
        fixture.SkipIfUnavailable();

        // Arrange: content over three pages, and a failure armed once the first page has been read, so it strikes after
        // the second page's rows are read.
        var failure = new FailOnceOnCloseInterceptor();
        DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
            .UseSqlServer(fixture.ConnectionString, sql => sql.ExecutionStrategy(dependencies => new RetryOnSimulatedFailure(dependencies)))
            .AddInterceptors(failure)
            .Options;
        var repository = new ResourceSqlRepository(new PooledDbContextFactory<ResourceDbContext>(options));
        string containerName = $"c-{Guid.NewGuid():N}";
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        byte[] payload = RandomNumberGenerator.GetBytes(65_535 * 40 + 7);
        await repository.AddResourceAsync(containerName, Version(containerName), new MemoryStream(payload));
        await using Stream stream = (await repository.GetResourceStreamAsync(containerName, "n"))!;
        using var copy = new MemoryStream();
        var first = new byte[1];
        await stream.ReadExactlyAsync(first);
        copy.Write(first);
        failure.Arm();

        // Act
        await stream.CopyToAsync(copy);

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(payload, copy.ToArray());
    }

    private sealed class SimulatedTransientException() : Exception("A simulated transient failure.");

    private sealed class RetryOnSimulatedFailure(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 1, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SimulatedTransientException;
    }

    /// <summary>
    /// Throws once, either on the given save of the upload or just after a transaction commits, once
    /// <paramref name="commitsBeforeFailing"/> commits have gone through.
    /// </summary>
    private sealed class FailOnceInterceptor(int failAtSave = 0, bool failAfterCommit = false, int commitsBeforeFailing = 0)
        : DbTransactionInterceptor, ISaveChangesInterceptor
    {
        private bool _armed;
        private int _saves;
        private int _commits;

        public bool Failed { get; private set; }

        public void Arm() => _armed = true;

        public ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed && !Failed && ++_saves == failAtSave) Fail();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (_armed && !Failed && failAfterCommit && _commits++ == commitsBeforeFailing) Fail();
            return Task.CompletedTask;
        }

        private void Fail()
        {
            Failed = true;
            throw new SimulatedTransientException();
        }
    }

    /// <summary>
    /// Throws once, when a connection closes after <see cref="Arm"/>: a download closes its connection after reading
    /// each page. (A failure as it opens wouldn't do: EF Core retries opening a connection by itself.)
    /// </summary>
    private sealed class FailOnceOnCloseInterceptor : DbConnectionInterceptor
    {
        private bool _armed;

        public bool Failed { get; private set; }

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
        {
            if (_armed && !Failed)
            {
                Failed = true;
                throw new SimulatedTransientException();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
