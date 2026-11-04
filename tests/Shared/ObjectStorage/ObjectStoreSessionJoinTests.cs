using Resourcefull.ObjectStorage;
using Resourcefull.Providers;

namespace Resourcefull.Tests.ObjectStorage;

/// <summary>
/// Tests of how an upload session on an object store joins its segments in the store, once they take as many parts as
/// the store joins. The S3 and Azure Blob suites run them on their own stores, through <see cref="InterferingStore"/>,
/// which lowers the store's limits and lets a test act around each join.
/// </summary>
public abstract class ObjectStoreSessionJoinTests
{
    /// <summary>Skips the test when the provider's store isn't available.</summary>
    protected abstract void SkipIfUnavailable();

    /// <summary>A client for the provider's test store.</summary>
    private protected abstract IObjectStore NewStore();

    /// <summary>The keys in the test store that start with <paramref name="prefix"/>.</summary>
    protected abstract Task<IReadOnlyList<string>> ListKeysAsync(string prefix);

    private async Task<int> CountKeysAsync(string prefix) => (await ListKeysAsync(prefix)).Count;

    /// <summary>How many segments the sessions under <paramref name="prefix"/> hold.</summary>
    private async Task<int> CountSegmentsAsync(string prefix) =>
        (await ListKeysAsync(prefix + "u/")).Count(key => key.Contains("/p/", StringComparison.Ordinal));

    private static async Task<ObjectStoreRepository> NewRepositoryAsync(string prefix, int segmentSize, IObjectStore store,
        long copyPartSize = ObjectParts.MaxJoinedPartSize)
    {
        var repository = new ObjectStoreRepository(store, prefix, null, null) { SegmentSize = segmentSize, CopyPartSize = copyPartSize };
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        return repository;
    }

    private static async Task<byte[]> ReadAllAsync(Stream? stream)
    {
        await using Stream content = stream!;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task UploadSession_ShouldJoinItsSegmentsInTheStoreAndComplete_WhenTheyTakeAsManyPartsAsTheStoreJoins()
    {
        SkipIfUnavailable();

        // Arrange: a store that joins at most three parts, segments of up to 9 MiB, and 40 MiB of content.
        string prefix = $"session-stages-{Guid.NewGuid():N}/";
        var store = new InterferingStore(NewStore()) { MaxParts = 3 };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store);
        var location = new ResourceLocation("c", "big.bin");
        byte[] payload = new byte[40 * 1024 * 1024];
        new Random(160).NextBytes(payload);
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));

        // Act
        long? stored = await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(payload));
        int segments = await CountSegmentsAsync(prefix);
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", payload.LongLength, "sha256:test");

        // Assert: the first three segments were joined into one, leaving three to join at completion, and the content reads
        // back whole.
        Assert.Equal(payload.LongLength, stored);
        Assert.Equal(3, segments);
        Assert.Equal(payload.LongLength, written!.ContentLength);
        Assert.Equal(payload, await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Fact]
    public async Task UploadSession_ShouldJoinOnlyTheSegmentsSinceTheLastJoin_WhenTheyTakeAsManyPartsAsTheStoreJoinsAgain()
    {
        SkipIfUnavailable();

        // Arrange: a store that joins at most four parts, joins in parts of up to 16 MiB, segments of up to 9 MiB, and
        // 63 MiB of content: four segments join into one of 36 MiB, which takes two parts, then two more into one of 18.
        string prefix = $"session-joins-{Guid.NewGuid():N}/";
        var store = new InterferingStore(NewStore()) { MaxParts = 4 };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store,
            copyPartSize: 2 * ObjectParts.PartSize);
        var location = new ResourceLocation("c", "big.bin");
        byte[] payload = new byte[63 * 1024 * 1024];
        new Random(1601).NextBytes(payload);
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));

        // Act
        long? stored = await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(payload));
        long copied = store.JoinedLength;
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", payload.LongLength, "sha256:test");

        // Assert: the 36 MiB segment wasn't copied again by the second join, and the content reads back whole.
        Assert.Equal(payload.LongLength, stored);
        Assert.Equal(54L * 1024 * 1024, copied);
        Assert.Equal(payload.LongLength, written!.ContentLength);
        Assert.Equal(payload, await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Fact]
    public async Task AppendStagedContentAsync_ShouldThrowUploadLimitExceptionWithoutJoining_WhenJoiningWouldLeaveNoRoom()
    {
        SkipIfUnavailable();

        // Arrange: a store that joins at most two parts, joins in parts of 8 MiB, and a session holding two segments of
        // 9 MiB, which joined would still take two.
        string prefix = $"session-full-{Guid.NewGuid():N}/";
        var store = new InterferingStore(NewStore()) { MaxParts = 2 };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store,
            copyPartSize: ObjectParts.PartSize);
        var location = new ResourceLocation("c", "big.bin");
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        long stored = (await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(new byte[18 * 1024 * 1024])))!.Value;

        // Act
        ResourceUploadLimitException refused = await Assert.ThrowsAsync<ResourceUploadLimitException>(() =>
            repository.AppendStagedContentAsync(location, "v1", stored, new MemoryStream("a"u8.ToArray())));

        // Assert: refused before copying anything, with what the session stored kept, which still completes.
        Assert.Equal(("c", "big.bin"), (refused.ContainerName, refused.ResourceName));
        Assert.Equal(0, store.JoinedLength);
        Assert.Equal(stored, (await repository.GetStagedVersionAsync(location, "v1"))!.StoredLength);
        Assert.NotNull(await repository.CompleteStagedVersionAsync(location, "v1", stored, "sha256:test"));
    }

    [Fact]
    public async Task AppendStagedContentAsync_ShouldThrowOffsetException_WhenAnotherAppendFromTheSameOffsetJoinsTheSegmentsFirst()
    {
        SkipIfUnavailable();

        // Arrange: a store that joins at most two parts, a session holding two segments of 9 MiB, and, as the next
        // append's join starts, another append from the same offset that joins them and stores one byte.
        string prefix = $"session-join-race-{Guid.NewGuid():N}/";
        var store = new InterferingStore(NewStore()) { MaxParts = 2 };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store);
        var location = new ResourceLocation("c", "big.bin");
        byte[] payload = new byte[18 * 1024 * 1024];
        new Random(1602).NextBytes(payload);
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(payload));
        bool raced = false;
        store.BeforeJoin = async () =>
        {
            if (raced) return;
            raced = true;
            await repository.AppendStagedContentAsync(location, "v1", payload.LongLength, new MemoryStream("b"u8.ToArray()));
        };

        // Act
        ResourceUploadOffsetException refused = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() =>
            repository.AppendStagedContentAsync(location, "v1", payload.LongLength, new MemoryStream("a"u8.ToArray())));
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", payload.LongLength + 1, "sha256:test");

        // Assert: refused with the other append's length, which is what the session holds.
        Assert.Equal(payload.LongLength + 1, refused.CurrentOffset);
        Assert.NotNull(written);
        Assert.Equal(payload.Append((byte)'b'), await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Fact]
    public async Task AppendStagedContentAsync_ShouldReturnNull_WhenTheSessionIsDeletedWhileItsSegmentsAreJoined()
    {
        SkipIfUnavailable();

        // Arrange: a store that joins at most two parts, a session holding two segments of 9 MiB, and the session
        // deleted as the next append's join starts.
        string prefix = $"session-join-deleted-{Guid.NewGuid():N}/";
        var store = new InterferingStore(NewStore()) { MaxParts = 2 };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store);
        var location = new ResourceLocation("c", "big.bin");
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        long stored = (await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(new byte[18 * 1024 * 1024])))!.Value;
        store.BeforeJoin = () => repository.DeleteStagedVersionAsync(location, "v1");

        // Act
        long? appended = await repository.AppendStagedContentAsync(location, "v1", stored, new MemoryStream("a"u8.ToArray()));

        // Assert: nothing of the session is left.
        Assert.Null(appended);
        Assert.Equal(0, await CountKeysAsync(prefix + "u/"));
    }

    [Fact]
    public async Task AppendStagedContentAsync_ShouldThrowOffsetExceptionAndDeleteItsJoin_WhenAnotherAppendRecordsItsJoinFirst()
    {
        SkipIfUnavailable();

        // Arrange: a store that joins at most two parts, a session holding two segments of 9 MiB, and, once the next
        // append's join is done but before it's recorded, another append from the same offset that joins them too,
        // records its join first and stores one byte.
        string prefix = $"session-joins-race-{Guid.NewGuid():N}/";
        var store = new InterferingStore(NewStore()) { MaxParts = 2 };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store);
        var location = new ResourceLocation("c", "big.bin");
        byte[] payload = new byte[18 * 1024 * 1024];
        new Random(1603).NextBytes(payload);
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(payload));
        bool raced = false;
        store.AfterJoin = async () =>
        {
            if (raced) return;
            raced = true;
            await repository.AppendStagedContentAsync(location, "v1", payload.LongLength, new MemoryStream("b"u8.ToArray()));
        };

        // Act
        ResourceUploadOffsetException refused = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() =>
            repository.AppendStagedContentAsync(location, "v1", payload.LongLength, new MemoryStream("a"u8.ToArray())));
        int segments = await CountSegmentsAsync(prefix);
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", payload.LongLength + 1, "sha256:test");

        // Assert: refused with the other append's length, and its own join deleted, leaving only the other append's
        // two segments.
        Assert.Equal(payload.LongLength + 1, refused.CurrentOffset);
        Assert.Equal(2, segments);
        Assert.NotNull(written);
        Assert.Equal(payload.Append((byte)'b'), await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppendStagedContentAsync_ShouldKeepWhatTheSessionStored_WhenCanceledAsItsSegmentsAreJoined(bool afterJoin)
    {
        SkipIfUnavailable();

        // Arrange: a store that joins at most two parts, a session holding two segments of 9 MiB, and the next append
        // canceled as the store starts joining them, or once the join is done.
        string prefix = $"session-join-canceled-{Guid.NewGuid():N}/";
        var store = new InterferingStore(NewStore()) { MaxParts = 2 };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store);
        var location = new ResourceLocation("c", "big.bin");
        byte[] payload = new byte[18 * 1024 * 1024];
        new Random(1604).NextBytes(payload);
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(payload));
        using var canceling = new CancellationTokenSource();
        Func<Task> cancel = () =>
        {
            canceling.Cancel();
            return Task.CompletedTask;
        };
        if (afterJoin) store.AfterJoin = cancel;
        else store.BeforeJoin = cancel;

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.AppendStagedContentAsync(location, "v1",
            payload.LongLength, new MemoryStream("a"u8.ToArray()), canceling.Token));
        store.AfterJoin = store.BeforeJoin = null;
        long? stored = (await repository.GetStagedVersionAsync(location, "v1"))?.StoredLength;
        int segments = await CountSegmentsAsync(prefix);
        long? appended = await repository.AppendStagedContentAsync(location, "v1", payload.LongLength, new MemoryStream("a"u8.ToArray()));
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", payload.LongLength + 1, "sha256:test");

        // Assert: the session still holds what it did, in its two segments or, when canceled after the join, in the one
        // they were joined into, and the same append, made again, completes it.
        Assert.Equal(payload.LongLength, stored);
        Assert.Equal(afterJoin ? 1 : 2, segments);
        Assert.Equal(payload.LongLength + 1, appended);
        Assert.NotNull(written);
        Assert.Equal(payload.Append((byte)'a'), await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }
}
