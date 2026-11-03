using Resourcefull.EntityFramework;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Resourcefull.PostgreSql;
using Resourcefull.Providers;

namespace Resourcefull.Tests.PostgreSql;

/// <summary>
/// Writes that the execution strategy runs again after a transient failure: one simulated at a chosen point, where the
/// commit went through but its acknowledgement was lost, and a real deadlock between two sessions' name locks.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class RetryIntegrationTests(PostgreSqlFixture fixture)
{
    // Several batches of chunks.
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(ResourceChunker.ChunkSize * 64);
    private static readonly string PayloadHash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Payload));
    private const long PartwayThroughTheSecondBatch =
        ResourceChunker.ChunkSize * (ResourcePostgreSqlRepository.ChunksPerUploadBatch + 4L);

    [Fact]
    public async Task AddResourceAsync_ShouldStoreTheContentOnce_WhenABatchIsStoredButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the second batch is stored, but its connection fails as it closes. The batch is sent again from what
        // the upload holds, since the content can't be read twice, and is refused as already there.
        var failure = new FailOnceOnCloseInterceptor();
        (ResourcePostgreSqlRepository repository, string c) = await NewRepositoryAsync(failure);
        var content = new ArmingStream(new NonSeekableStream(Payload), PartwayThroughTheSecondBatch, failure.Arm);

        // Act
        WrittenContent written = await repository.AddResourceAsync(Version(c), content);

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(PayloadHash, written.ContentHash);
        Assert.Equal(Payload, await ReadAsync(repository, c));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldKeepItsStagedVersion_WhenItsInsertIsStoredButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the staged version's insert, the upload's first statement, commits as it runs, then its connection
        // fails as it closes, so the retry finds the version already there and takes it as its own.
        var failure = new FailOnceOnCloseInterceptor();
        (ResourcePostgreSqlRepository repository, string c) = await NewRepositoryAsync(failure);
        failure.Arm();

        // Act
        WrittenContent written = await repository.AddResourceAsync(Version(c), new NonSeekableStream(Payload));

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(PayloadHash, written.ContentHash);
        Assert.True(Assert.Single(await repository.GetResourceVersionsAsync(new ResourceLocation(c, "n"))).IsLatest);
        Assert.Equal(Payload, await ReadAsync(repository, c));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldReturnWhatWasStoredWithoutStoringItAgain_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: create-only too, which must not conflict with its own stored version.
        var failure = new FailOnceAfterCommitInterceptor();
        (ResourcePostgreSqlRepository repository, string c) = await NewRepositoryAsync(failure);
        failure.Arm();

        // Act
        WrittenContent written = await repository.AddResourceAsync(Version(c), new NonSeekableStream(Payload),
            new ResourceWriteCondition { CreateOnly = true });

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(PayloadHash, written.ContentHash);
        Assert.True(Assert.Single(await repository.GetResourceVersionsAsync(new ResourceLocation(c, "n"))).IsLatest);
        Assert.Equal(Payload, await ReadAsync(repository, c));
    }

    [Fact]
    public async Task AddContainerAsync_ShouldCreateTheContainer_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the retry's insert is refused, which aborts its transaction, so it must not report a conflict.
        var failure = new FailOnceAfterCommitInterceptor();
        (ResourcePostgreSqlRepository repository, _) = await NewRepositoryAsync(failure);
        failure.Arm();
        string created = $"c-{Guid.NewGuid():N}";

        // Act
        await repository.AddContainerAsync(created, new Dictionary<string, string>());

        // Assert
        Assert.True(failure.Failed);
        Assert.NotNull(await repository.GetContainerInfoAsync(created));
    }

    [Fact]
    public async Task AppendStagedContentAsync_ShouldStoreEachBatchOnce_WhenABatchCommitsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a session's first batch commits, then its acknowledgement is lost, so it's sent again and finds
        // itself stored.
        var failure = new FailOnceAfterCommitInterceptor();
        (ResourcePostgreSqlRepository repository, string c) = await NewRepositoryAsync(failure);
        var location = new ResourceLocation(c, "n");
        await repository.StartStagedVersionAsync(Version(c));
        failure.Arm();

        // Act
        long? stored = await repository.AppendStagedContentAsync(location, "v1", 0, new NonSeekableStream(Payload));

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(Payload.Length, stored);
        await using Stream content = (await repository.GetStagedStreamAsync(location, "v1"))!;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        Assert.Equal(Payload, copy.ToArray());
    }

    [Fact]
    public async Task CompleteStagedVersionAsync_ShouldReturnWhatItStored_WhenItCommitsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: the completion commits, then its acknowledgement is lost, so the retry finds the session gone, made
        // current.
        var failure = new FailOnceAfterCommitInterceptor();
        (ResourcePostgreSqlRepository repository, string c) = await NewRepositoryAsync(failure);
        var location = new ResourceLocation(c, "n");
        await repository.StartStagedVersionAsync(Version(c));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(Payload));
        failure.Arm();

        // Act
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", Payload.Length, PayloadHash);

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal((Payload.LongLength, PayloadHash), (written!.ContentLength, written.ContentHash));
        Assert.True(Assert.Single(await repository.GetResourceVersionsAsync(location)).IsLatest);
        Assert.Null(await repository.GetStagedVersionAsync(location, "v1"));
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldReturnWhatItStoredWithoutCopyingAgain_WhenTheCommitSucceedsButItsAcknowledgementIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        var failure = new FailOnceAfterCommitInterceptor();
        (ResourcePostgreSqlRepository repository, string c) = await NewRepositoryAsync(failure);
        await repository.AddResourceAsync(Version(c), new MemoryStream(Payload));
        failure.Arm();
        var copy = new NewResourceVersion(new ResourceLocation(c, "copy"), "v2");

        // Act
        WrittenContent? written = await repository.CopyResourceAsync(new ResourceLocation(c, "n"), "v1", copy, new ResourceWriteCondition());

        // Assert
        Assert.True(failure.Failed);
        Assert.Equal(Payload.Length, written!.ContentLength);
        Assert.Equal(["v2"], (await repository.GetResourceVersionsAsync(new ResourceLocation(c, "copy"))).Select(v => v.VersionId));
    }

    [Fact]
    public async Task UsePostgreSql_ShouldRetryTheWriteAndCountTheRetry_WhenItsNameLockIsADeadlockVictim()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a rename locks both its names, "a" then "b". The opponent holds "b", then waits for "a", closing
        // the cycle; its own deadlock check waits a minute, so the rename's runs first and aborts the rename.
        using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container =
            await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync($"c-{Guid.NewGuid():N}");
        await container.UploadAsync("a", BinaryData.FromString("a"));
        long a = ResourcePostgreSqlRepository.NameLockKey(new ResourceLocation(container.Name, "a"));
        long b = ResourcePostgreSqlRepository.NameLockKey(new ResourceLocation(container.Name, "b"));
        // Other tests may retry at the same time, so only a deadlock's SQLSTATE is looked for.
        var retries = new ConcurrentQueue<string?>();
        using MeterListener listener = ListenForRetries(retries);
        await using var opponent = new NpgsqlConnection(fixture.ConnectionString);
        await opponent.OpenAsync();
        await using NpgsqlTransaction transaction = await opponent.BeginTransactionAsync();
        await LockAsync(opponent, b, "SET LOCAL deadlock_timeout = '1min';");

        // Act
        Task<ResourceInfo?> rename = container.RenameResourceAsync("a", "b");
        await WaitForAWaitOnAsync(b);
        await LockAsync(opponent, a);
        await transaction.CommitAsync();
        ResourceInfo? renamed = await rename;

        // Assert
        Assert.Equal("b", renamed!.Name);
        Assert.Contains("40P01", retries);
        Assert.Equal("a", (await container.DownloadContentAsync("b"))!.Content.ToString());
    }

    /// <summary>Takes the advisory lock <paramref name="key"/> in the connection's transaction, waiting up to 30 seconds.</summary>
    private static async Task LockAsync(NpgsqlConnection connection, long key, string before = "")
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = $"{before} SELECT pg_advisory_xact_lock(@key);";
        command.Parameters.AddWithValue("key", key);
        command.CommandTimeout = 30;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Waits, for up to 30 seconds, until a session is waiting for the advisory lock <paramref name="key"/>.</summary>
    private async Task WaitForAWaitOnAsync(long key)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        // A 64-bit key is held as its high half in classid and its low half in objid.
        command.CommandText = """
            SELECT COUNT(*) FROM pg_locks
            WHERE locktype = 'advisory' AND NOT granted AND ((classid::bigint << 32) | objid::bigint) = @key
            """;
        command.Parameters.AddWithValue("key", key);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while ((long)(await command.ExecuteScalarAsync())! == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "No session waited for the lock.");
            await Task.Delay(50);
        }
    }

    /// <summary>Adds the SQLSTATE of each retry counted in <c>resourcefull.postgresql.retries</c> to <paramref name="retries"/>.</summary>
    private static MeterListener ListenForRetries(ConcurrentQueue<string?> retries)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.postgresql.retries") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Key == "db.response.status_code") retries.Enqueue(tag.Value as string);
        });
        listener.Start();
        return listener;
    }

    private async Task<(ResourcePostgreSqlRepository, string)> NewRepositoryAsync(IInterceptor interceptor)
    {
        DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.ExecutionStrategy(dependencies => new RetryOnSimulatedFailure(dependencies)))
            .AddInterceptors(interceptor)
            .Options;
        var repository = new ResourcePostgreSqlRepository(new ResourceDbContextFactory(options));
        string containerName = $"c-{Guid.NewGuid():N}";
        await repository.AddContainerAsync(containerName, new Dictionary<string, string>());
        return (repository, containerName);
    }

    private static NewResourceVersion Version(string containerName) => new(new ResourceLocation(containerName, "n"), "v1");

    private static async Task<byte[]> ReadAsync(ResourcePostgreSqlRepository repository, string containerName)
    {
        await using Stream stream = (await repository.GetResourceStreamAsync(new ResourceLocation(containerName, "n")))!;
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

    /// <summary>Throws once, just after a transaction commits, once <see cref="Arm"/> has been called.</summary>
    private sealed class FailOnceAfterCommitInterceptor : DbTransactionInterceptor
    {
        private bool _armed;

        public bool Failed { get; private set; }

        public void Arm() => _armed = true;

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_armed && !Failed)
            {
                Failed = true;
                throw new SimulatedTransientException();
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Throws once, when a connection closes after <see cref="Arm"/>, so a statement that committed as it ran is
    /// reported as failed. (A failure as it opens wouldn't do: EF Core retries opening a connection by itself.)
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

    /// <summary>
    /// Calls <paramref name="arm"/> once <paramref name="armAt"/> bytes have been read, so a failure strikes the next
    /// thing the upload does, which is storing the batch those bytes are in.
    /// </summary>
    private sealed class ArmingStream(Stream inner, long armAt, Action arm) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            if (_read < armAt && _read + read >= armAt) arm();
            _read += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
