using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Resourcefull.ObjectStorage;
using Resourcefull.Tests.ObjectStorage;
using Resourcefull.Providers;
using Resourcefull.S3;

namespace Resourcefull.Tests.S3;

[Collection(S3Collection.Name)]
public class S3IntegrationTests(S3Fixture fixture)
{
    private async Task<int> CountKeysAsync(string prefix)
    {
        ListObjectsV2Response listed = await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix
        });
        return listed.S3Objects?.Count ?? 0;
    }

    [Fact]
    public async Task UploadAsync_ShouldRetryAndCountTheRetry_WhenAnotherWriterChangesTheNameFirst()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name with one version, and a client that changes the name's head just before the next write of it.
        var interfering = DispatchProxy.Create<IAmazonS3, InterferingS3>();
        ((InterferingS3)(object)interfering).Target = fixture.Client;
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3(fixture.BucketName, s3 =>
        {
            s3.Client = interfering;
            s3.KeyPrefix = $"retry-{Guid.NewGuid():N}/";
        }).UseVersioning());
        await using ServiceProvider provider = services.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a.txt", BinaryData.FromString("first"));
        var retries = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.s3.retries") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => retries.Add(value));
        listener.Start();
        ((InterferingS3)(object)interfering).Interfere = true;

        // Act
        await container.UploadAsync("a.txt", BinaryData.FromString("second"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert: the write lost the first race, tried again, and both versions are kept.
        Assert.Equal([1L], retries);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("a.txt")).Count);
        Assert.Equal("second", (await container.DownloadContentAsync("a.txt"))!.Content.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_ShouldKeepBothNames_WhenOneNameIsAnotherWhoseKeyEndsAtASegment(bool longerFirst)
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name whose escaped form fills a segment, and the same name with more after it, which a store that
        // keeps keys as files would put under the first one as a directory.
        await using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateContainerAsync($"c-{Guid.NewGuid():N}");
        string shorter = new('a', ObjectKeys.SegmentBytes - ObjectKeys.LongestUnit + 1), longer = shorter + "b";
        string[] order = longerFirst ? [longer, shorter] : [shorter, longer];

        // Act
        foreach (string name in order) await container.UploadAsync(name, BinaryData.FromString(name.Length.ToString()));

        // Assert
        Assert.Equal([shorter.Length.ToString(), longer.Length.ToString()], [(await container.DownloadContentAsync(shorter))!.Content.ToString(),
            (await container.DownloadContentAsync(longer))!.Content.ToString()]);
        Assert.Equal([shorter, longer], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreContentInParts_WhenItIsLargerThanOnePart()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a part and a byte, so the upload takes two parts.
        await using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateContainerAsync($"c-{Guid.NewGuid():N}");
        byte[] payload = new byte[ObjectParts.PartSize + 1];
        new Random(1042).NextBytes(payload);

        // Act
        ResourceInfo stored = await container.UploadAsync("large.bin", new MemoryStream(payload));

        // Assert
        Assert.Equal(payload.LongLength, stored.ContentLength);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)), stored.ContentHash);
        Assert.Equal(payload, (await container.DownloadContentAsync("large.bin"))!.Content.ToArray());
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(40)]
    public async Task AddResourceAsync_ShouldStoreTheContentInPiecesTheStoreJoins_WhenItIsLargerThanOneUploadTakes(int mebibytes)
    {
        fixture.SkipIfUnavailable();

        // Arrange: a store that takes 16 MiB in one upload, and content of one, two, or two and a half uploads' worth.
        string prefix = $"pieces-{Guid.NewGuid():N}/";
        var store = new S3ObjectStore(fixture.Client, fixture.BucketName) { MaxUploadLength = 2 * ObjectParts.PartSize };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);
        byte[] payload = new byte[mebibytes * 1024 * 1024];
        new Random(159).NextBytes(payload);
        var location = new ResourceAddress("c", "big.bin");

        // Act
        WrittenContent written = await repository.AddResourceAsync(new NewResourceVersion(location, "v1"), new MemoryStream(payload));

        // Assert: the content whole, with its hash, and only the name's head and one content object left, and no record of
        // the pieces.
        Assert.Equal(payload.LongLength, written.ContentLength);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)), written.ContentHash);
        Assert.Equal(payload, await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
        Assert.Equal(2, await CountKeysAsync(prefix + "d/"));
        Assert.Equal(0, await CountKeysAsync(prefix + "p/"));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldLeaveNothingBehind_WhenJoiningThePiecesFails()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a store that takes 16 MiB in one upload and fails every join, and 40 MiB of content.
        string prefix = $"pieces-failed-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName) { MaxUploadLength = 2 * ObjectParts.PartSize })
        {
            BeforeJoin = () => throw new IOException("Joining failed.")
        };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);

        // Act
        Exception? failed = await Record.ExceptionAsync(() => repository.AddResourceAsync(
            new NewResourceVersion(new ResourceAddress("c", "big.bin"), "v1"), new MemoryStream(new byte[40 * 1024 * 1024])));

        // Assert: neither the pieces nor their record is left.
        Assert.IsType<IOException>(failed);
        Assert.Equal(0, await CountKeysAsync(prefix + "d/"));
        Assert.Equal(0, await CountKeysAsync(prefix + "p/"));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowUploadLimitExceptionAndLeaveNothingBehind_WhenTheContentIsLargerThanAnObject()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a store that takes 16 MiB in one upload and keeps 24 MiB in one object, and 40 MiB of content.
        string prefix = $"pieces-limit-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName) { MaxUploadLength = 2 * ObjectParts.PartSize })
        {
            MaxObject = 3 * ObjectParts.PartSize
        };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);

        // Act
        Exception? refused = await Record.ExceptionAsync(() => repository.AddResourceAsync(
            new NewResourceVersion(new ResourceAddress("c", "big.bin"), "v1"), new MemoryStream(new byte[40 * 1024 * 1024])));

        // Assert
        Assert.IsType<ResourceUploadLimitException>(refused);
        Assert.Equal(0, await CountKeysAsync(prefix + "d/"));
        Assert.Equal(0, await CountKeysAsync(prefix + "p/"));
    }

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldDeleteAStoppedUploadsPieces_WhenItsRecordIsOverAnHourOld()
    {
        fixture.SkipIfUnavailable();

        // Arrange: content no head names, a record of pieces listing it, as an upload that stopped leaves, and a store
        // that lists the record as two hours old and the content as three days old.
        string prefix = $"pieces-stopped-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName))
        {
            MapListing = item => item with
            {
                LastModified = item.LastModified - (item.Key.Contains("/p/", StringComparison.Ordinal) ? TimeSpan.FromHours(2) : TimeSpan.FromDays(3))
            }
        };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock, store: store);
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceAddress("c", "a.txt"), "v1"),
            new MemoryStream("kept"u8.ToArray()));
        string named = (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "d/"
        })).S3Objects.Single(o => o.Key.Contains("/b/", StringComparison.Ordinal)).Key;
        string piece = named[..(named.LastIndexOf('/') + 1)] + new string('0', 32);
        string record = $"{prefix}p/{new string('0', 32)}";
        await fixture.Client.PutObjectAsync(new PutObjectRequest { BucketName = fixture.BucketName, Key = piece, ContentBody = "x" });
        await fixture.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = fixture.BucketName, Key = record, ContentBody = new JsonObject { ["k"] = new JsonArray(piece) }.ToJsonString()
        });

        // Act: two sweeps a day apart.
        await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        int recordAfterFirst = await CountKeysAsync(record);
        clock.Now = DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1);
        await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));

        // Assert: the stale record kept nothing and was deleted, so the piece went as any unnamed content does.
        Assert.Equal(0, recordAfterFirst);
        Assert.Equal(0, await CountKeysAsync(piece));
    }

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldKeepAnUploadsPieces_WhileTheUploadIsStillGoing()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a store that takes 16 MiB in one upload and lists content as three days old, and an upload of 40 MiB
        // that runs a sweep just before its pieces are joined.
        string prefix = $"pieces-kept-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName) { MaxUploadLength = 2 * ObjectParts.PartSize })
        {
            MapListing = item => item.Key.Contains("/b/", StringComparison.Ordinal)
                ? item with { LastModified = item.LastModified.AddDays(-3) }
                : item
        };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);
        store.BeforeJoin = () => repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var location = new ResourceAddress("c", "big.bin");
        byte[] payload = new byte[40 * 1024 * 1024];
        new Random(160).NextBytes(payload);

        // Act
        await repository.AddResourceAsync(new NewResourceVersion(location, "v1"), new MemoryStream(payload));
        using GetObjectResponse scan = await fixture.Client.GetObjectAsync(fixture.BucketName, prefix + "s");

        // Assert: the scan noted none of the pieces as unnamed, and the upload went through whole.
        Assert.Empty(JsonNode.Parse(scan.ResponseStream)!["u"]!.AsArray());
        Assert.Equal(payload, await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Fact]
    public async Task ConditionalWrites_ShouldSucceedOnce_WhenAWriteLandsButItsAnswerIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a client that, when told to, sends the next conditional write and then answers 412, as a store does
        // to the SDK's resend of a write whose answer was lost.
        var interfering = DispatchProxy.Create<IAmazonS3, InterferingS3>();
        var proxy = (InterferingS3)(object)interfering;
        proxy.Target = fixture.Client;
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3(fixture.BucketName, s3 =>
        {
            s3.Client = interfering;
            s3.KeyPrefix = $"lost-{Guid.NewGuid():N}/";
        }));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();

        // Act
        proxy.LoseAnswer = true;
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        proxy.LoseAnswer = true;
        ResourceInfo stored = await container.UploadAsync("a.txt", BinaryData.FromString("x"));
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("a.txt");
        proxy.LoseAnswer = true;
        bool deleted = await container.DeleteResourceIfExistsAsync("a.txt");

        // Assert: each write was taken as done, once, and none was answered as a conflict.
        Assert.Equal(stored.VersionId, Assert.Single(versions).VersionId);
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("a.txt"));
        Assert.False(proxy.LoseAnswer);
    }

    [Fact]
    public async Task ConditionalWrites_ShouldSucceedOnce_WhenAWriteLandsButAnotherWriterChangesItBeforeItsLostAnswerIsChecked()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a client that, when told to, sends the next conditional write, lets another writer rewrite the
        // object, and then answers 412, so the write can't be recognized by its ID.
        var interfering = DispatchProxy.Create<IAmazonS3, InterferingS3>();
        var proxy = (InterferingS3)(object)interfering;
        proxy.Target = fixture.Client;
        string prefix = $"lost-changed-{Guid.NewGuid():N}/";
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3(fixture.BucketName, s3 =>
        {
            s3.Client = interfering;
            s3.KeyPrefix = prefix;
        }));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();

        // Act: the creation and the upload are followed by another write of their object, the deletion by the
        // container being created again, under a new ID.
        proxy.AnotherWrite = json => json["w"] = Guid.NewGuid().ToString("N");
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        proxy.AnotherWrite = json => json["w"] = Guid.NewGuid().ToString("N");
        ResourceInfo stored = await container.UploadAsync("a.txt", BinaryData.FromString("x"));
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("a.txt");
        proxy.AnotherWrite = json =>
        {
            json["id"] = Guid.NewGuid().ToString("N");
            json["deleted"] = false;
            json["w"] = Guid.NewGuid().ToString("N");
        };
        bool deleted = await resources.DeleteContainerIfExistsAsync("c");

        // Assert: each write was taken as done, once: neither the creation nor the upload was answered as a conflict,
        // the deletion didn't report finding nothing to delete, and the container created again was left alone, while
        // what the deleted one held is gone.
        Assert.Equal(stored.VersionId, Assert.Single(versions).VersionId);
        Assert.True(deleted);
        Assert.True(await resources.ContainerExistsAsync("c"));
        Assert.Equal(0, await CountKeysAsync(prefix + "d/"));
        Assert.Null(proxy.AnotherWrite);
    }

    [Fact]
    public async Task GetExpiredAsync_ShouldDropAStaleEntryOnlyOnceItIsAnHourOld_WhenItsVersionHasAnotherToken()
    {
        fixture.SkipIfUnavailable();

        // Arrange: an expired version, plus a copy of its entry under another token, like the one an expiry change leaves
        // when it stops after writing its entry; and a repository on the same keys whose real time the test controls.
        string prefix = $"expiry-{Guid.NewGuid():N}/";
        await using ServiceProvider provider = fixture.BuildServiceProvider(keyPrefix: prefix);
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        var expiresAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await container.UploadAsync("a.txt", BinaryData.FromString("a"), new ResourceUploadOptions { ExpiresAt = expiresAt });
        string entry = (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "x/"
        })).S3Objects.Single().Key;
        string[] parts = entry.Split('/');
        parts[^3] = new string('0', 32);
        await fixture.Client.CopyObjectAsync(fixture.BucketName, entry, fixture.BucketName, string.Join('/', parts));
        var clock = new MovableClock();
        var repository = new ObjectStoreRepository(new S3ObjectStore(fixture.Client, fixture.BucketName), prefix, null, null) { RealTime = clock };

        // Act
        clock.Now = DateTimeOffset.UtcNow.AddMinutes(30);
        IReadOnlyList<ResourceInfo> whileYoung = await repository.GetExpiredAsync(DateTimeOffset.UtcNow, 100);
        int entriesWhileYoung = await CountKeysAsync(prefix + "x/");
        clock.Now = DateTimeOffset.UtcNow.AddHours(2);
        IReadOnlyList<ResourceInfo> onceOld = await repository.GetExpiredAsync(DateTimeOffset.UtcNow, 100);
        int entriesOnceOld = await CountKeysAsync(prefix + "x/");

        // Assert: the version is listed once, by its own entry; the stale copy stays while it might belong to a write in
        // progress, then goes.
        Assert.Equal("a.txt", Assert.Single(whileYoung).Name);
        Assert.Equal("a.txt", Assert.Single(onceOld).Name);
        Assert.Equal((2, 1), (entriesWhileYoung, entriesOnceOld));
    }

    [Fact]
    public async Task SetExpiryAndDeletes_ShouldLeaveNoEntryBehind_WhenTheVersionMovesOn()
    {
        fixture.SkipIfUnavailable();

        // Arrange: two names with an expiry each.
        string prefix = $"entries-{Guid.NewGuid():N}/";
        await using ServiceProvider provider = fixture.BuildServiceProvider(keyPrefix: prefix);
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        var expiresAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ResourceInfo a = await container.UploadAsync("a.txt", BinaryData.FromString("a"), new ResourceUploadOptions { ExpiresAt = expiresAt });
        await container.UploadAsync("b.txt", BinaryData.FromString("b"), new ResourceUploadOptions { ExpiresAt = expiresAt });
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();

        // Act
        await container.SetExpiryAsync("a.txt", null, expiresAt.AddDays(1));
        int afterChange = await CountKeysAsync(prefix + "x/");
        await container.DeleteResourceIfExistsAsync("b.txt");
        int afterDelete = await CountKeysAsync(prefix + "x/");
        bool expired = await repository.DeleteExpiredVersionAsync(new Resourcefull.Providers.ResourceAddress("c", "a.txt"), a.VersionId,
            DateTimeOffset.UtcNow);

        // Assert: changing the expiry replaced the entry rather than adding one, and each delete took its version's entry
        // and content, leaving only the names' empty heads.
        Assert.Equal((2, 1), (afterChange, afterDelete));
        Assert.True(expired);
        Assert.Equal(0, await CountKeysAsync(prefix + "x/"));
        Assert.Equal(2, await CountKeysAsync(prefix + "d/"));
    }

    /// <summary>A repository on the test bucket under <paramref name="prefix"/>, with a container named c.</summary>
    private async Task<ObjectStoreRepository> NewRepositoryAsync(string prefix, TimeProvider? realTime = null, int? segmentSize = null,
        IObjectStore? store = null, IReadOnlySet<string>? searchableMetadataKeys = null)
    {
        var repository = new ObjectStoreRepository(store ?? new S3ObjectStore(fixture.Client, fixture.BucketName), prefix, null, null,
            searchableMetadataKeys)
        {
            RealTime = realTime ?? TimeProvider.System, SegmentSize = segmentSize ?? 64 * 1024 * 1024
        };
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
    public async Task UploadSession_ShouldJoinItsSegmentsInOrder_WhenItsAppendsSpanSeveral()
    {
        fixture.SkipIfUnavailable();

        // Arrange: segments of up to 9 MiB, and appends of 1 MiB, 20 MiB and 3 bytes. The second is stored with the
        // first's short segment, in three segments, and the third with the second's short last one.
        string prefix = $"session-{Guid.NewGuid():N}/";
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024);
        var location = new ResourceAddress("c", "big.bin");
        byte[] payload = new byte[21 * 1024 * 1024 + 3];
        new Random(151).NextBytes(payload);
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));

        // Act
        long offset = 0;
        foreach (int length in new[] { 1024 * 1024, 20 * 1024 * 1024, 3 })
        {
            var part = new MemoryStream(payload, (int)offset, length);
            offset = (await repository.AppendStagedContentAsync(location, "v1", offset, part))!.Value;
        }
        List<long> segments = [.. (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "u/"
        })).S3Objects.Where(o => o.Key.Contains("/p/", StringComparison.Ordinal)).Select(o => o.Size ?? 0).Order()];
        byte[] staged = await ReadAllAsync(await repository.GetStagedStreamAsync(location, "v1"));
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", offset, "sha256:test");
        byte[] stored = await ReadAllAsync(await repository.GetResourceStreamAsync(location));

        // Assert: three segments, with no short one but the last; the content, whole and in order, before and after
        // completing; and nothing left of the session.
        Assert.Equal([3 * 1024 * 1024 + 3, 9 * 1024 * 1024, 9 * 1024 * 1024], segments);
        Assert.Equal(payload, staged);
        Assert.Equal(payload.LongLength, written!.ContentLength);
        Assert.Equal(payload, stored);
        Assert.Equal(0, await CountKeysAsync(prefix + "u/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task AppendStagedContentAsync_ShouldRefuseAndKeepNothing_WhenAnotherAppendFromTheSameOffsetLandsFirst(string stored)
    {
        fixture.SkipIfUnavailable();

        // Arrange: a session holding nothing, or a short segment that both appends start by storing again; and an append
        // whose content holds back its first read until the test lets it go.
        string prefix = $"race-{Guid.NewGuid():N}/";
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix);
        var location = new ResourceAddress("c", "a.txt");
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        if (stored.Length > 0) await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(Encoding.UTF8.GetBytes(stored)));
        var held = new HeldStream(new MemoryStream("first"u8.ToArray()));

        // Act: the held append has checked the offset; another from the same offset lands; then the held one goes on.
        Task<long?> first = repository.AppendStagedContentAsync(location, "v1", stored.Length, held);
        await held.Reading;
        long? second = await repository.AppendStagedContentAsync(location, "v1", stored.Length, new MemoryStream("second"u8.ToArray()));
        held.Release();
        ResourceUploadOffsetException refused = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() => first);

        // Assert: only the second append's content is stored, and the held one left no segment of its own.
        Assert.Equal(stored.Length + 6, second);
        Assert.Equal(stored.Length + 6, refused.CurrentOffset);
        Assert.Equal(Encoding.UTF8.GetBytes(stored + "second"), await ReadAllAsync(await repository.GetStagedStreamAsync(location, "v1")));
        Assert.Equal(2, await CountKeysAsync(prefix + "u/"));
    }

    [Fact]
    public async Task CompleteStagedVersionAsync_ShouldReturnWhatTheOtherAdded_WhenAnotherCompletionOfTheSessionLandsFirst()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a session with content, on a store that holds back the first join once it's done, as a slow
        // completion that a client retried would be.
        string prefix = $"complete-race-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName)) { HoldFirstJoin = true };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);
        var location = new ResourceAddress("c", "a.txt");
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream("a"u8.ToArray()));

        // Act: the held completion has joined; the other completes in full; then the held one goes on.
        Task<WrittenContent?> first = repository.CompleteStagedVersionAsync(location, "v1", 1, "sha256:test");
        await store.Joined;
        WrittenContent? second = await repository.CompleteStagedVersionAsync(location, "v1", 1, "sha256:test");
        store.Release();
        WrittenContent? firstResult = await first;

        // Assert: both report the one version, and only its content is left: the name's head and one content object.
        Assert.NotNull(second);
        Assert.Equal(second, firstResult);
        Assert.Single(await repository.GetResourceVersionsAsync(location));
        Assert.Equal("a"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
        Assert.Equal(2, await CountKeysAsync(prefix + "d/"));
        Assert.Equal(0, await CountKeysAsync(prefix + "u/"));
    }

    [Fact]
    public async Task AppendStagedContentAsync_ShouldBeRefusedUntilTheCompletionIsFinished_WhenACompletionStoppedAfterSealing()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a session with content, whose record is sealed, as a completion that stopped after sealing leaves it.
        string prefix = $"sealed-{Guid.NewGuid():N}/";
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix);
        var location = new ResourceAddress("c", "a.txt");
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream("a"u8.ToArray()));
        string recordKey = (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "u/"
        })).S3Objects.Single(o => o.Key.EndsWith("/r", StringComparison.Ordinal)).Key;
        using (GetObjectResponse read = await fixture.Client.GetObjectAsync(fixture.BucketName, recordKey))
        {
            JsonNode record = JsonNode.Parse(read.ResponseStream)!;
            record["s"] = Guid.NewGuid().ToString("N");
            await fixture.Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = fixture.BucketName, Key = recordKey, ContentBody = record.ToJsonString()
            });
        }

        // Act
        ResourceUploadOffsetException refused = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() =>
            repository.AppendStagedContentAsync(location, "v1", 1, new MemoryStream("b"u8.ToArray())));
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", 1, "sha256:test");

        // Assert: the append was refused, and completing again finished the completion.
        Assert.Equal(1, refused.CurrentOffset);
        Assert.Equal(1, written!.ContentLength);
        Assert.Equal("a"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Fact]
    public async Task CompleteStagedVersionAsync_ShouldLeaveTheSessionTakingAppends_WhenTheCompletionIsRefused()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name with a current version, and a session for it.
        string prefix = $"unseal-{Guid.NewGuid():N}/";
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix);
        var location = new ResourceAddress("c", "a.txt");
        await repository.AddResourceAsync(new NewResourceVersion(location, "v0"), new MemoryStream("old"u8.ToArray()));
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream("a"u8.ToArray()));

        // Act: a create-only completion, then another append.
        await Assert.ThrowsAsync<ResourceConflictException>(() => repository.CompleteStagedVersionAsync(location, "v1", 1, "sha256:test",
            new ResourceWriteCondition { CreateOnly = true }));
        long? appended = await repository.AppendStagedContentAsync(location, "v1", 1, new MemoryStream("b"u8.ToArray()));

        // Assert: the session took the append, and the refused completion left no content behind: under the container's
        // data are only the name's head and its old version's content.
        Assert.Equal(2, appended);
        Assert.Equal(2, await CountKeysAsync(prefix + "d/"));
    }

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldDeleteADeletedContainersSessionsAndOldOrphanedSegments_WhenCalled()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a session in a container that's then deleted, a live session in another, and a segment no record
        // names, as a cancel whose delete failed leaves.
        string prefix = $"sweep-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock);
        await repository.AddContainerAsync("d", new Dictionary<string, string>());
        var gone = new ResourceAddress("c", "a.txt");
        var live = new ResourceAddress("d", "a.txt");
        foreach (ResourceAddress location in new[] { gone, live })
        {
            await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
            await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream("a"u8.ToArray()));
        }
        await repository.DeleteContainerAsync("c");
        await fixture.Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = fixture.BucketName, Key = $"{prefix}u/{new string('0', 32)}/v9!/p/{new string('0', 32)}", ContentBody = "x"
        });

        // Act
        int whileYoung = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        int keysWhileYoung = await CountKeysAsync(prefix + "u/");
        clock.Now = DateTimeOffset.UtcNow.AddHours(2);
        int onceOld = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));

        // Assert: the deleted container's session went at once and the orphaned segment once it was an hour old; the
        // live session stays, its record and its segment.
        Assert.Equal((1, 3), (whileYoung, keysWhileYoung));
        Assert.Equal(0, onceOld);
        Assert.Equal(2, await CountKeysAsync(prefix + "u/"));
        Assert.NotNull(await repository.GetStagedVersionAsync(live, "v1"));
    }

    [Fact]
    public async Task AppendStagedContentAsync_ShouldThrowUploadLimitException_WhenTheSessionHoldsAsMuchAsAnObject()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a store that keeps 24 MiB in one object, and a session given 40 MiB.
        string prefix = $"session-limit-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName)) { MaxObject = 3 * ObjectParts.PartSize };
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, segmentSize: 9 * 1024 * 1024, store: store);
        var location = new ResourceAddress("c", "big.bin");
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));

        // Act
        ResourceUploadLimitException refused = await Assert.ThrowsAsync<ResourceUploadLimitException>(() =>
            repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(new byte[40 * 1024 * 1024])));

        // Assert: refused, naming the resource, with exactly an object's worth stored, which still completes.
        Assert.Equal(("c", "big.bin"), (refused.ContainerName, refused.ResourceName));
        Assert.Equal(3 * ObjectParts.PartSize, (await repository.GetStagedVersionAsync(location, "v1"))!.StoredLength);
        Assert.NotNull(await repository.CompleteStagedVersionAsync(location, "v1", 3 * ObjectParts.PartSize, "sha256:test"));
    }

    /// <summary>Whether a conditional write is of a move's entry, deciding it as <paramref name="state"/>.</summary>
    private static bool Decides(string prefix, string key, byte[] body, int state) =>
        key.StartsWith(prefix + "m/", StringComparison.Ordinal) && JsonNode.Parse(body)!["s"]!.GetValue<int>() == state;

    /// <summary>Whether any head under <paramref name="prefix"/> still has a move's mark.</summary>
    private async Task<bool> AnyHeadMarkedAsync(string prefix)
    {
        foreach (S3Object head in (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "d/"
        })).S3Objects ?? [])
        {
            using GetObjectResponse read = await fixture.Client.GetObjectAsync(fixture.BucketName, head.Key);
            if (head.Key.Contains("/h/", StringComparison.Ordinal) && new StreamReader(read.ResponseStream).ReadToEnd().Contains("\"mv\""))
                return true;
        }
        return false;
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldCopyTheContentInParts_WhenMovedToAnotherContainer()
    {
        fixture.SkipIfUnavailable();

        // Arrange: copies in parts of 8 MiB, and a version of 17 MiB, which takes two, the last 1 MiB going with the second.
        string prefix = $"move-parts-{Guid.NewGuid():N}/";
        var repository = new ObjectStoreRepository(new S3ObjectStore(fixture.Client, fixture.BucketName), prefix, null, null)
        {
            CopyPartSize = ObjectParts.PartSize
        };
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        await repository.AddContainerAsync("d", new Dictionary<string, string>());
        byte[] payload = new byte[17 * 1024 * 1024];
        new Random(154).NextBytes(payload);
        var from = new ResourceAddress("c", "big.bin");
        var to = new ResourceAddress("d", "big.bin");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream(payload));

        // Act
        bool moved = await repository.MoveResourceAsync(from, to, null);

        // Assert
        Assert.True(moved);
        Assert.Equal(payload, await ReadAllAsync(await repository.GetResourceStreamAsync(to)));
        Assert.Equal(0, (await repository.GetContainerUsageAsync("c")).VersionCount);
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldLeaveTheVersionsAtTheDestinationAndTheSweepFinishIt_WhenTheMoverStopsAfterCommitting()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name with two versions, and a store that fails every write from the moment a move is committed, as
        // a mover that stops there leaves things.
        string prefix = $"move-committed-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock, store: store);
        await repository.AddContainerAsync("d", new Dictionary<string, string>());
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("d", "b.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("first"u8.ToArray()));
        await repository.AddResourceAsync(new NewResourceVersion(from, "v2"), new MemoryStream("second"u8.ToArray()));
        bool committing = false;
        int writesSinceCommitting = 0;
        // The commit itself lands; every write after it fails.
        store.FailWrites = _ => committing && writesSinceCommitting++ > 0;
        store.BeforePut = (key, body) =>
        {
            committing |= Decides(prefix, key, body, 1);
            return Task.CompletedTask;
        };

        // Act
        bool moved = await repository.MoveResourceAsync(from, to, null);
        IReadOnlyList<ResourceInfo> atSource = await repository.GetResourceVersionsAsync(from);
        IReadOnlyList<ResourceInfo> atDestination = await repository.GetResourceVersionsAsync(to);
        bool markedBeforeSweep = await AnyHeadMarkedAsync(prefix);
        store.FailWrites = null;
        clock.Now = DateTimeOffset.UtcNow.AddHours(2);
        var recovered = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.s3.moves_recovered") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => recovered.Add(value));
        listener.Start();
        await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));

        // Assert: the move happened, and readers see it although both heads were still marked; the sweep then cleared
        // the marks and the entry, deleted the source's content, and counted the move.
        Assert.True(moved);
        Assert.Equal([1L], recovered);
        Assert.Empty(atSource);
        Assert.Equal(["v2", "v1"], atDestination.Select(v => v.VersionId));
        Assert.True(markedBeforeSweep);
        Assert.False(await AnyHeadMarkedAsync(prefix));
        Assert.Equal(0, await CountKeysAsync(prefix + "m/"));
        Assert.Equal((0, 2), ((await repository.GetContainerUsageAsync("c")).VersionCount, (await repository.GetContainerUsageAsync("d")).VersionCount));
        Assert.Equal("first"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(to, "v1")));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldLeaveTheVersionsAtTheSourceAndTheSweepUndoIt_WhenTheMoverStopsBeforeCommitting()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name, and a store that fails every write from the moment a move is about to be committed.
        string prefix = $"move-pending-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock, store: store);
        await repository.AddContainerAsync("d", new Dictionary<string, string>());
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("d", "b.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("first"u8.ToArray()));
        bool stopped = false;
        store.FailWrites = _ => stopped;
        store.BeforePut = (key, body) =>
        {
            if (Decides(prefix, key, body, 1)) stopped = true;
            return Task.CompletedTask;
        };

        // Act
        await Assert.ThrowsAsync<IOException>(() => repository.MoveResourceAsync(from, to, null));
        IReadOnlyList<ResourceInfo> atSource = await repository.GetResourceVersionsAsync(from);
        IReadOnlyList<ResourceInfo> atDestination = await repository.GetResourceVersionsAsync(to);
        bool markedBeforeSweep = await AnyHeadMarkedAsync(prefix);
        stopped = false;
        clock.Now = DateTimeOffset.UtcNow.AddHours(2);
        await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));

        // Assert: the move didn't happen, and readers see that although both heads were still marked; the sweep then
        // abandoned it, cleared the marks and the entry, and deleted the copy of the content.
        Assert.Equal("v1", Assert.Single(atSource).VersionId);
        Assert.Empty(atDestination);
        Assert.True(markedBeforeSweep);
        Assert.False(await AnyHeadMarkedAsync(prefix));
        Assert.Equal(0, await CountKeysAsync(prefix + "m/"));
        Assert.Equal((1, 0), ((await repository.GetContainerUsageAsync("c")).VersionCount, (await repository.GetContainerUsageAsync("d")).VersionCount));
        Assert.Equal("first"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(from)));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldStartAgainAndMove_WhenTheSweepTakesItWhileItCopies()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name, and a store whose first copy waits while a sweep runs with the clock two hours ahead, as if the
        // copy took that long.
        string prefix = $"move-swept-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock, store: store);
        await repository.AddContainerAsync("d", new Dictionary<string, string>());
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("d", "a.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("moved"u8.ToArray()));
        bool swept = false;
        store.BeforeJoin = async () =>
        {
            if (swept) return;
            swept = true;
            clock.Now = DateTimeOffset.UtcNow.AddHours(2);
            await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        };

        // Act
        bool moved = await repository.MoveResourceAsync(from, to, null);

        // Assert: the swept try left nothing behind, and the next one moved the version, with one copy of its content.
        Assert.True(swept);
        Assert.True(moved);
        Assert.Empty(await repository.GetResourceVersionsAsync(from));
        Assert.Equal("moved"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(to)));
        Assert.False(await AnyHeadMarkedAsync(prefix));
        Assert.Equal(0, await CountKeysAsync(prefix + "m/"));
        Assert.Equal((0, 1), ((await repository.GetContainerUsageAsync("c")).VersionCount, (await repository.GetContainerUsageAsync("d")).VersionCount));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldLeaveNoMarkAndBeRefused_WhenTheSweepTakesItAndAnotherWriterTakesTheDestinationBeforeItMarks()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name, and a store that, just before a move marks the source, runs a sweep with the clock two hours
        // ahead and uploads to the destination name, as if the mover stalled that long.
        string prefix = $"move-stalled-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock, store: store);
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("c", "b.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("first"u8.ToArray()));
        bool stalled = false;
        store.BeforePut = async (key, body) =>
        {
            if (stalled || JsonNode.Parse(body) is not JsonObject json || json["mv"]?["in"]?.GetValue<bool>() != false) return;
            stalled = true;
            clock.Now = DateTimeOffset.UtcNow.AddHours(2);
            await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
            await repository.AddResourceAsync(new NewResourceVersion(to, "v9"), new MemoryStream("other"u8.ToArray()));
        };

        // Act
        Exception? refused = await Record.ExceptionAsync(() => repository.MoveResourceAsync(from, to, null));

        // Assert: the try marked only the source, which it then cleared; the next try found the destination taken.
        Assert.True(stalled);
        Assert.IsType<ResourceConflictException>(refused);
        Assert.Equal("v1", Assert.Single(await repository.GetResourceVersionsAsync(from)).VersionId);
        Assert.Equal("first"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(from)));
        Assert.False(await AnyHeadMarkedAsync(prefix));
        Assert.Equal(0, await CountKeysAsync(prefix + "m/"));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldKeepTheSourcesVersions_WhenASweptTrysMarkOutlivesItsEntry()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a move the sweep takes just before the move marks the source, while another upload takes the
        // destination name; the store then fails the try's write that clears its mark, so the mark outlives the move's
        // entry.
        string prefix = $"move-orphaned-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock, store: store);
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("c", "b.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("first"u8.ToArray()));
        bool stalled = false;
        store.BeforePut = async (key, body) =>
        {
            if (stalled || JsonNode.Parse(body) is not JsonObject json || json["mv"]?["in"]?.GetValue<bool>() != false) return;
            stalled = true;
            clock.Now = DateTimeOffset.UtcNow.AddHours(2);
            await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
            await repository.AddResourceAsync(new NewResourceVersion(to, "v9"), new MemoryStream("other"u8.ToArray()));
            // The mark itself lands; every later write of the source head fails.
            int writes = 0;
            store.FailWrites = k => k == key && writes++ > 0;
        };

        // Act
        Exception? refused = await Record.ExceptionAsync(() => repository.MoveResourceAsync(from, to, null));
        bool markedBeforeUpload = await AnyHeadMarkedAsync(prefix);
        store.FailWrites = null;
        await repository.AddResourceAsync(new NewResourceVersion(from, "v2"), new MemoryStream("second"u8.ToArray()));

        // Assert: the mark left behind read as the abandoned try it was, so the upload kept the version already there,
        // and cleared the mark.
        Assert.IsType<ResourceConflictException>(refused);
        Assert.True(markedBeforeUpload);
        Assert.Equal(["v2", "v1"], (await repository.GetResourceVersionsAsync(from)).Select(v => v.VersionId));
        Assert.False(await AnyHeadMarkedAsync(prefix));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldWriteItsEntryAgain_WhenACopyTakesAQuarterHour()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a move across containers on a clock the test moves on, and a store whose copy moves that clock on a
        // quarter hour, then waits until the move writes its entry again.
        string prefix = $"move-refresh-{Guid.NewGuid():N}/";
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, time, store: store);
        await repository.AddContainerAsync("d", new Dictionary<string, string>());
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("d", "a.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("moved"u8.ToArray()));
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool copying = false;
        store.BeforePut = (key, body) =>
        {
            if (copying && Decides(prefix, key, body, 0)) refreshed.TrySetResult();
            return Task.CompletedTask;
        };
        store.BeforeJoin = async () =>
        {
            copying = true;
            time.Advance(TimeSpan.FromMinutes(16));
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };

        // Act
        bool moved = await repository.MoveResourceAsync(from, to, null);

        // Assert: the entry was written again while the copy ran, and the move went through.
        Assert.True(refreshed.Task.IsCompletedSuccessfully);
        Assert.True(moved);
        Assert.Equal("moved"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(to)));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldStartAgainAndMoveEveryVersion_WhenAnotherWriterChangesTheSourceBeforeItCommits()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name, and a store that holds back a move's commit while the test uploads another version to it.
        string prefix = $"move-source-race-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("c", "b.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("first"u8.ToArray()));
        bool raced = false;
        store.BeforePut = async (key, body) =>
        {
            if (raced || !Decides(prefix, key, body, 1)) return;
            raced = true;
            await repository.AddResourceAsync(new NewResourceVersion(from, "v2"), new MemoryStream("second"u8.ToArray()));
        };

        // Act
        bool moved = await repository.MoveResourceAsync(from, to, null);

        // Assert: the upload abandoned the first try; the next one moved both versions.
        Assert.True(moved);
        Assert.Empty(await repository.GetResourceVersionsAsync(from));
        Assert.Equal(["v2", "v1"], (await repository.GetResourceVersionsAsync(to)).Select(v => v.VersionId));
        Assert.False(await AnyHeadMarkedAsync(prefix));
        Assert.Equal(0, await CountKeysAsync(prefix + "m/"));
    }

    [Fact]
    public async Task FindResourcePageAsync_ShouldFindTheLastWritersValue_WhenAnotherWriterReplacesTheNameDuringItsWrite()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name with customer 42, and a store that lets an upload of customer 7 land just before the head write
        // of another upload of customer 42. The upload of 7 deletes the entry for 42, which the version current at the
        // end needs.
        string prefix = $"search-race-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store,
            searchableMetadataKeys: new HashSet<string> { "customer" });
        var name = new ResourceAddress("c", "a.txt");
        NewResourceVersion Version(string id, string customer) =>
            new(name, id) { Metadata = new Dictionary<string, string> { ["customer"] = customer } };
        await repository.AddResourceAsync(Version("v1", "42"), new MemoryStream("1"u8.ToArray()));
        bool raced = false;
        store.BeforePut = async (key, body) =>
        {
            if (raced || !key.Contains("/h/", StringComparison.Ordinal)) return;
            raced = true;
            await repository.AddResourceAsync(Version("v2", "7"), new MemoryStream("2"u8.ToArray()));
        };

        // Act
        await repository.AddResourceAsync(Version("v3", "42"), new MemoryStream("3"u8.ToArray()));
        IReadOnlyList<ResourceInfo> found = await repository.FindResourcePageAsync("c", null, null, 10,
            new ResourceMetadataMatch("customer", "42"));

        // Assert
        Assert.True(raced);
        Assert.Equal(["v3"], found.Select(r => r.VersionId));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldBeAbandonedAndRefused_WhenAnotherWriterCreatesTheDestinationBeforeItCommits()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name, and a store that holds back a move's commit while the test uploads to the destination name.
        string prefix = $"move-race-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);
        var from = new ResourceAddress("c", "a.txt");
        var to = new ResourceAddress("c", "b.txt");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream("moved"u8.ToArray()));
        bool raced = false;
        store.BeforePut = async (key, body) =>
        {
            if (raced || !Decides(prefix, key, body, 1)) return;
            raced = true;
            await repository.AddResourceAsync(new NewResourceVersion(to, "v9"), new MemoryStream("other"u8.ToArray()),
                new ResourceWriteCondition { CreateOnly = true });
        };

        // Act
        Exception? refused = await Record.ExceptionAsync(() => repository.MoveResourceAsync(from, to, null));

        // Assert: the upload abandoned the move, which then found the destination taken; nothing moved, and nothing of
        // the move is left.
        Assert.IsType<ResourceConflictException>(refused);
        Assert.Equal("v1", Assert.Single(await repository.GetResourceVersionsAsync(from)).VersionId);
        Assert.Equal("v9", Assert.Single(await repository.GetResourceVersionsAsync(to)).VersionId);
        Assert.False(await AnyHeadMarkedAsync(prefix));
        Assert.Equal(0, await CountKeysAsync(prefix + "m/"));
    }

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldDeleteContentNoHeadNames_WhenTwoScansADayApartFindIt()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a version; content no head names, as a write that stopped after uploading it would leave; and an object
        // a deleted container's unfinished purge left behind.
        string prefix = $"unnamed-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock);
        var location = new ResourceAddress("c", "a.txt");
        await repository.AddResourceAsync(new NewResourceVersion(location, "v1"), new MemoryStream("kept"u8.ToArray()));
        string named = (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "d/"
        })).S3Objects.Single(o => o.Key.Contains("/b/", StringComparison.Ordinal)).Key;
        string unnamed = named[..(named.LastIndexOf('/') + 1)] + new string('0', 32);
        await fixture.Client.PutObjectAsync(new PutObjectRequest { BucketName = fixture.BucketName, Key = unnamed, ContentBody = "x" });
        await repository.AddContainerAsync("gone", new Dictionary<string, string>());
        await repository.DeleteContainerAsync("gone");
        using GetObjectResponse record = await fixture.Client.GetObjectAsync(fixture.BucketName, prefix + "c/gone!");
        string left = $"{prefix}d/{JsonNode.Parse(record.ResponseStream)!["id"]}/b/{new string('0', 32)}";
        await fixture.Client.PutObjectAsync(new PutObjectRequest { BucketName = fixture.BucketName, Key = left, ContentBody = "x" });

        var counted = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.s3.abandoned_uploads") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => counted.Add(value));
        listener.Start();

        // Act: sweeps two days on, half a day after that, and a day after the first.
        clock.Now = DateTimeOffset.UtcNow.AddDays(2);
        int first = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        int unnamedAfterFirst = await CountKeysAsync(unnamed);
        int leftAfterFirst = await CountKeysAsync(left);
        clock.Now = DateTimeOffset.UtcNow.AddDays(2.5);
        int tooSoon = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        int unnamedAfterTooSoon = await CountKeysAsync(unnamed);
        clock.Now = DateTimeOffset.UtcNow.AddDays(3).AddMinutes(1);
        int second = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));

        // Assert: the first scan only noted the unnamed content, a sweep within the day didn't scan, and the next scan
        // deleted it and counted it; the deleted container's object went at the first; the named version is untouched.
        Assert.Equal((0, 1, 0), (first, unnamedAfterFirst, leftAfterFirst));
        Assert.Equal((0, 1), (tooSoon, unnamedAfterTooSoon));
        Assert.Equal(1, second);
        Assert.Equal([1L], counted);
        Assert.Equal(0, await CountKeysAsync(unnamed));
        Assert.Equal("kept"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldPutTheBinsIndexRight_WhenAScanFindsItWrong()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a.txt is in the bin and b.txt isn't. Then the index goes wrong three ways: a bin entry no version
        // records (as a failed delete leaves), a mark for b.txt as if it had only a bin, and no mark for a.txt (as a
        // failed write leaves).
        string prefix = $"bin-index-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock);
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceAddress("c", "a.txt"), "v1"), new MemoryStream("a"u8.ToArray()));
        await repository.SoftDeleteResourceAsync(new ResourceAddress("c", "a.txt"), null, null, DateTimeOffset.UtcNow.AddDays(30));
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceAddress("c", "b.txt"), "v1"), new MemoryStream("b"u8.ToArray()));
        List<string> keys = [.. (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "d/"
        })).S3Objects.Select(o => o.Key)];
        string entry = keys.Single(k => k.Contains("/t/", StringComparison.Ordinal));
        string markOfA = keys.Single(k => k.Contains("/e/", StringComparison.Ordinal));
        string stale = $"{entry[..(entry.IndexOf("/t/", StringComparison.Ordinal) + 3)]}0000000000000000001/v9!/{new string('0', 32)}/5";
        string markOfB = markOfA.Replace("a!2Etxt!", "b!2Etxt!", StringComparison.Ordinal);
        await fixture.Client.PutObjectAsync(new PutObjectRequest { BucketName = fixture.BucketName, Key = stale, ContentBody = "" });
        await fixture.Client.PutObjectAsync(new PutObjectRequest { BucketName = fixture.BucketName, Key = markOfB, ContentBody = "" });
        await fixture.Client.DeleteObjectAsync(fixture.BucketName, markOfA);

        // Act: a scan two days on.
        clock.Now = DateTimeOffset.UtcNow.AddDays(2);
        await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));

        // Assert
        Assert.Equal((1, 0, 1, 0), (await CountKeysAsync(entry), await CountKeysAsync(stale), await CountKeysAsync(markOfA),
            await CountKeysAsync(markOfB)));
        ResourceContainerUsage usage = await repository.GetContainerUsageAsync("c");
        Assert.Equal((1L, 1L, 1L, 1L), (usage.ResourceCount, usage.VersionCount, usage.DeletedVersionCount, usage.DeletedSize.Bytes));
    }

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldLeaveContentNoHeadNamesAlone_WhenItIsUnderADayOld()
    {
        fixture.SkipIfUnavailable();

        // Arrange: content no head names, and a repository whose real time the test sets.
        string prefix = $"unnamed-young-{Guid.NewGuid():N}/";
        var clock = new MovableClock();
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, clock);
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceAddress("c", "a.txt"), "v1"),
            new MemoryStream("kept"u8.ToArray()));
        string named = (await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix + "d/"
        })).S3Objects.Single(o => o.Key.Contains("/b/", StringComparison.Ordinal)).Key;
        string unnamed = named[..(named.LastIndexOf('/') + 1)] + new string('0', 32);
        await fixture.Client.PutObjectAsync(new PutObjectRequest { BucketName = fixture.BucketName, Key = unnamed, ContentBody = "x" });

        // Act: scans half a day on, while the content is under a day old, then a day and a half on, when it's over a day
        // old.
        clock.Now = DateTimeOffset.UtcNow.AddHours(12);
        await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));
        clock.Now = DateTimeOffset.UtcNow.AddHours(36).AddMinutes(1);
        int second = await repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1));

        // Assert: the first scan passed over the young content, so the second, the first to find it old, only noted it.
        Assert.Equal(0, second);
        Assert.Equal(1, await CountKeysAsync(unnamed));
    }

    [Fact]
    public async Task DeleteAbandonedUploadsAsync_ShouldDeleteAndCountTheOthers_WhenOneSessionCantBeRead()
    {
        fixture.SkipIfUnavailable();

        // Arrange: two sessions in a container that's then deleted, on a store that then fails every read of one's record.
        string prefix = $"sweep-failure-{Guid.NewGuid():N}/";
        var store = new InterferingStore(new S3ObjectStore(fixture.Client, fixture.BucketName));
        ObjectStoreRepository repository = await NewRepositoryAsync(prefix, store: store);
        foreach ((string name, string versionId) in new[] { ("a.txt", "v1"), ("b.txt", "v2") })
        {
            var location = new ResourceAddress("c", name);
            await repository.StartStagedVersionAsync(new NewResourceVersion(location, versionId));
            await repository.AppendStagedContentAsync(location, versionId, 0, new MemoryStream("a"u8.ToArray()));
        }
        await repository.DeleteContainerAsync("c");
        store.FailReads = key => key.EndsWith("/v1!/r", StringComparison.Ordinal);
        var counted = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.s3.abandoned_uploads") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => counted.Add(value));
        listener.Start();

        // Act
        AggregateException failed = await Assert.ThrowsAsync<AggregateException>(() =>
            repository.DeleteAbandonedUploadsAsync(DateTimeOffset.UtcNow.AddDays(-1)));

        // Assert: the session that could be read was deleted and counted; the other is left for the next sweep, its
        // record and its segment.
        Assert.IsType<IOException>(Assert.Single(failed.InnerExceptions));
        Assert.Equal([1L], counted);
        Assert.Equal(2, await CountKeysAsync(prefix + "u/"));
    }

    /// <summary>A stream whose first read waits for <see cref="Release"/>, so a test can act while it's being read.</summary>
    private sealed class HeldStream(Stream inner) : Stream
    {
        private readonly TaskCompletionSource _reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when the first read starts.</summary>
        public Task Reading => _reading.Task;

        public void Release() => _released.TrySetResult();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reading.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A clock the test sets.</summary>
    private sealed class MovableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task DeleteContainerAsync_ShouldDeleteEverythingTheContainerHeld_WhenCalled()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string prefix = $"delete-{Guid.NewGuid():N}/";
        await using ServiceProvider provider = fixture.BuildServiceProvider(keyPrefix: prefix);
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        for (int i = 0; i < 3; i++) await container.UploadAsync($"{i}.txt", BinaryData.FromString("x"));
        int before = await CountKeysAsync(prefix + "d/");

        // Act
        bool deleted = await resources.DeleteContainerIfExistsAsync("c");

        // Assert: its record stays behind, marked deleted, and nothing it held does.
        Assert.True(deleted);
        Assert.Equal(6, before);
        Assert.Equal(0, await CountKeysAsync(prefix + "d/"));
        Assert.False(await resources.ContainerExistsAsync("c"));
    }

    [Fact]
    public async Task UseS3_ShouldKeepEachStoreToItsOwnKeys_WhenTwoShareABucketUnderDifferentPrefixes()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        await using ServiceProvider first = fixture.BuildServiceProvider(keyPrefix: $"first-{Guid.NewGuid():N}/");
        await using ServiceProvider second = fixture.BuildServiceProvider(keyPrefix: $"second-{Guid.NewGuid():N}/");

        // Act
        await first.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("shared-name");
        await second.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("shared-name");

        // Assert: neither saw the other's container, so neither creation conflicted.
        Assert.True(await first.GetRequiredService<IResourceServiceClient>().ContainerExistsAsync("shared-name"));
        Assert.True(await second.GetRequiredService<IResourceServiceClient>().ContainerExistsAsync("shared-name"));
    }

    [Fact]
    public async Task GetInfoAsync_ShouldThrowRatherThanFindNothing_WhenTheBucketDoesntExist()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a bucket name nobody created, as a mistyped setting would give.
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3($"missing-{Guid.NewGuid():N}", s3 => s3.Client = fixture.Client));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Task act() => provider.GetRequiredService<IResourceServiceClient>().GetContainerClient("c").GetInfoAsync();

        // Assert
        Assert.Equal("NoSuchBucket", (await Assert.ThrowsAsync<AmazonS3Exception>(act)).ErrorCode);
    }

    [Fact]
    public void UseS3_ShouldThrow_WhenTheKeyPrefixIsTooLong()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        void act() => services.AddResourcefull(options => options.UseS3("bucket", s3 => s3.KeyPrefix = new string('p', 191)));

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    /// <summary>
    /// Passes every call to <see cref="Target"/>, except the next conditional write while a flag or the action is set.
    /// With <see cref="LoseAnswer"/>, that write is sent and then answered with 412, as a store answers the SDK's
    /// resend of a write whose answer was lost. With <see cref="AnotherWrite"/>, another writer also rewrites the
    /// object in between, changing its JSON as that action does. With <see cref="Interfere"/>, the next conditional
    /// write to a head is preceded by an unconditional rewrite of that head, as another writer would make: the same
    /// content with a space appended, so its ETag changes and the conditional write fails. Each flag and the action
    /// clear themselves once used.
    /// </summary>
    public class InterferingS3 : DispatchProxy
    {
        public IAmazonS3 Target { get; set; } = null!;

        public bool Interfere { get; set; }

        public bool LoseAnswer { get; set; }

        public Action<JsonNode>? AnotherWrite { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAmazonS3.PutObjectAsync) && args![0] is PutObjectRequest request
                && (request.IfMatch is not null || request.IfNoneMatch is not null))
            {
                if (LoseAnswer || AnotherWrite is not null)
                {
                    Action<JsonNode>? anotherWrite = AnotherWrite;
                    LoseAnswer = false;
                    AnotherWrite = null;
                    return PutThenLoseTheAnswerAsync(request, anotherWrite, (CancellationToken)args[1]!);
                }
                if (Interfere && request.IfMatch is not null && request.Key.Contains("/h/", StringComparison.Ordinal))
                {
                    Interfere = false;
                    return InterfereThenPutAsync(request, (CancellationToken)args[1]!);
                }
            }
            return targetMethod.Invoke(Target, args);
        }

        private async Task<PutObjectResponse> PutThenLoseTheAnswerAsync(PutObjectRequest request, Action<JsonNode>? anotherWrite,
            CancellationToken cancellationToken)
        {
            await Target.PutObjectAsync(request, cancellationToken);
            if (anotherWrite is not null)
            {
                using GetObjectResponse written = await Target.GetObjectAsync(request.BucketName, request.Key, cancellationToken);
                JsonNode json = (await JsonNode.ParseAsync(written.ResponseStream, cancellationToken: cancellationToken))!;
                anotherWrite(json);
                await Target.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = request.BucketName, Key = request.Key, ContentBody = json.ToJsonString()
                }, cancellationToken);
            }
            throw new AmazonS3Exception("At least one of the pre-conditions you specified did not hold")
            {
                StatusCode = System.Net.HttpStatusCode.PreconditionFailed, ErrorCode = "PreconditionFailed"
            };
        }

        private async Task<PutObjectResponse> InterfereThenPutAsync(PutObjectRequest request, CancellationToken cancellationToken)
        {
            using GetObjectResponse current = await Target.GetObjectAsync(request.BucketName, request.Key, cancellationToken);
            using var reader = new StreamReader(current.ResponseStream);
            await Target.PutObjectAsync(new PutObjectRequest
            {
                BucketName = request.BucketName, Key = request.Key, ContentBody = await reader.ReadToEndAsync(cancellationToken) + " "
            }, cancellationToken);
            return await Target.PutObjectAsync(request, cancellationToken);
        }
    }
}
