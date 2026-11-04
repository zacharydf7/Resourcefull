using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    private static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    /// <summary>A provider whose containers keep deleted versions for <see cref="KeptFor"/>, on a clock the test moves.</summary>
    private ServiceProvider BuildBinProvider(ManualTimeProvider clock, Action<ResourcefullOptionsBuilder>? configure = null,
        ResourceContainerPolicy? policy = null) =>
        BuildServiceProvider(clock, o =>
        {
            o.UseExpiry().UseContainerPolicies(policy ?? new ResourceContainerPolicy { KeepDeletedFor = KeptFor }, TimeSpan.Zero);
            configure?.Invoke(o);
        });

    private static Task<List<ResourceInfo>> ListDeletedAsync(IResourceContainerClient container, string? prefix = null, int pageSize = 1000) =>
        container.ListDeletedAsync(new ResourceListOptions { Prefix = prefix, PageSize = pageSize }).ToListAsync().AsTask();

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldMoveEveryVersionToTheBin_WhenTheContainerKeepsDeletedVersions()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo older = await container.UploadAsync("a", BinaryData.FromString("older"));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("current!"), new ResourceUploadOptions
        {
            Mode = ResourceWriteMode.Replace, Metadata = new Dictionary<string, string> { ["k"] = "v" }
        });

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("a");

        // Assert: gone from every read, and in the bin with its attributes.
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("a"));
        Assert.Empty(await container.GetResourceVersionsAsync("a"));
        Assert.Empty(await container.ListResourcesAsync().ToListAsync());
        List<ResourceInfo> bin = await ListDeletedAsync(container);
        Assert.Equal(new[] { older.VersionId, current.VersionId }.Order(StringComparer.Ordinal), bin.Select(v => v.VersionId));
        Assert.All(bin, v => Assert.Equal((container.Name, "a", Kept(clock.Now), Kept(clock.Now + KeptFor), false),
            (v.ContainerName, v.Name, v.DeletedAt, v.PurgeAt, v.IsLatest)));
        ResourceInfo binned = bin.Single(v => v.VersionId == current.VersionId);
        Assert.Equal((current.ContentHash, 8L, "v"), (binned.ContentHash, binned.ContentLength, binned.Metadata["k"]));
        ResourceContainerUsage usage = await container.GetUsageAsync();
        Assert.Equal((0L, 0L, 0L, 13L, 2L), (usage.StoredSize.Bytes, usage.ResourceCount, usage.VersionCount,
            usage.DeletedSize.Bytes, usage.DeletedVersionCount));
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldDeleteForGood_WhenTheContainerKeepsNothing()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, policy: new ResourceContainerPolicy { KeepDeletedFor = TimeSpan.Zero });
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));

        // Act
        await container.DeleteResourceIfExistsAsync("a");

        // Assert
        Assert.Empty(await ListDeletedAsync(container));
        Assert.False(await container.UndeleteAsync("a"));
    }

    [Fact]
    public async Task UploadAsync_ShouldCreateTheName_WhenItsVersionsAreAllInTheBin()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo deleted = await container.UploadAsync("a", BinaryData.FromString("old"));
        await container.DeleteResourceIfExistsAsync("a");

        // Act
        ResourceInfo created = await container.UploadAsync("a", BinaryData.FromString("new"), new ResourceUploadOptions { Mode = ResourceWriteMode.Create });

        // Assert
        Assert.Equal([created.VersionId], (await container.GetResourceVersionsAsync("a")).Select(v => v.VersionId));
        Assert.Equal([deleted.VersionId], (await ListDeletedAsync(container)).Select(v => v.VersionId));
    }

    [Fact]
    public async Task ImportVersionAsync_ShouldRefuseTheVersionId_WhenTheBinHasIt()
    {
        Requires(SupportsSoftDelete, "a bin");
        Requires(SupportsImport, "imports");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo deleted = await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.DeleteResourceIfExistsAsync("a");
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        var version = new NewResourceVersion(new ResourceLocation(container.Name, "a"), deleted.VersionId) { LastModified = clock.Now };

        // Act & Assert
        await Assert.ThrowsAsync<ResourceConflictException>(() => repository.ImportVersionAsync(version, new MemoryStream([1])));
        Assert.False(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task UndeleteAsync_ShouldBringBackEveryVersionAndMakeTheNewestCurrent_WhenTheNameHasNoCurrentVersion()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo older = await container.UploadAsync("a", BinaryData.FromString("older"));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("current"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await container.DeleteResourceIfExistsAsync("a");

        // Act
        bool undeleted = await container.UndeleteAsync("a");

        // Assert
        Assert.True(undeleted);
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("a");
        Assert.Equal([current.VersionId, older.VersionId], versions.Select(v => v.VersionId));
        Assert.All(versions, v => Assert.Equal((null, null), (v.DeletedAt, v.PurgeAt)));
        Assert.Equal("current", (await container.DownloadContentAsync("a"))!.Content.ToString());
        Assert.Empty(await ListDeletedAsync(container));
        Assert.Equal((2L, 0L), ((await container.GetUsageAsync()).VersionCount, (await container.GetUsageAsync()).DeletedVersionCount));
    }

    [Fact]
    public async Task UndeleteAsync_ShouldBringTheVersionBackAsAnOlderOne_WhenTheNameHasACurrentVersion()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange: the current version goes to the bin, and the one before it becomes current.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("a", BinaryData.FromString("first"));
        clock.Now = clock.Now.AddSeconds(1);
        ResourceInfo second = await container.UploadAsync("a", BinaryData.FromString("second"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await container.DeleteResourceIfExistsAsync("a", second.VersionId);

        // Act
        bool undeleted = await container.UndeleteAsync("a", second.VersionId);

        // Assert: the current version stays current, and the other comes back behind it, ready to take over.
        Assert.True(undeleted);
        Assert.Equal([first.VersionId, second.VersionId], (await container.GetResourceVersionsAsync("a")).Select(v => v.VersionId));
        Assert.Equal(first.VersionId, (await container.GetResourceInfoAsync("a"))!.VersionId);
        await container.DeleteResourceIfExistsAsync("a", first.VersionId);
        Assert.Equal(second.VersionId, (await container.GetResourceInfoAsync("a"))!.VersionId);
    }

    [Fact]
    public async Task UndeleteAsync_ShouldReturnFalse_WhenTheBinHasNoneOfIt()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo live = await container.UploadAsync("a", BinaryData.FromString("a"));

        // Act & Assert
        Assert.False(await container.UndeleteAsync("a"));
        Assert.False(await container.UndeleteAsync("a", live.VersionId));
        Assert.False(await container.UndeleteAsync("missing"));
        Assert.False(await container.PurgeDeletedAsync("a"));
        Assert.True(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task UndeleteAsync_ShouldRefuseAndBringNothingBack_WhenTheContainerHasNoRoomForTheName()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange: the bin doesn't count, so another name takes the one place.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, policy: new ResourceContainerPolicy { KeepDeletedFor = KeptFor, MaxResourceCount = 1 });
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.DeleteResourceIfExistsAsync("a");
        await container.UploadAsync("b", BinaryData.FromString("b"));

        // Act
        ResourcePolicyViolationException refused = await Assert.ThrowsAsync<ResourcePolicyViolationException>(() => container.UndeleteAsync("a"));

        // Assert
        Assert.Equal(ResourcePolicyRule.MaxResourceCount, refused.Rule);
        Assert.False(await container.ExistsAsync("a"));
        Assert.Single(await ListDeletedAsync(container));
    }

    [Fact]
    public async Task PurgeDeletedAsync_ShouldDeleteOnlyTheVersionGivenForGood_WhenGivenAVersionId()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo older = await container.UploadAsync("a", BinaryData.FromString("older"));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("current"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await container.DeleteResourceIfExistsAsync("a");

        // Act
        bool purged = await container.PurgeDeletedAsync("a", current.VersionId);

        // Assert
        Assert.True(purged);
        Assert.Equal([older.VersionId], (await ListDeletedAsync(container)).Select(v => v.VersionId));
        Assert.False(await container.UndeleteAsync("a", current.VersionId));
        Assert.Equal((5L, 1L), ((await container.GetUsageAsync()).DeletedSize.Bytes, (await container.GetUsageAsync()).DeletedVersionCount));
    }

    [Fact]
    public async Task PurgeDeletedAsync_ShouldPurgeOnlyWhatsDueAndReportIt_WhenTheSweepRuns()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange: one version is due to leave the bin, and one deleted later isn't yet.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseCleanupHandler<RecordingCleanupHandler>());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo due = await container.UploadAsync("due", BinaryData.FromString("due"));
        await container.DeleteResourceIfExistsAsync("due");
        clock.Now = clock.Now.AddDays(1);
        ResourceInfo kept = await container.UploadAsync("kept", BinaryData.FromString("kept"));
        await container.DeleteResourceIfExistsAsync("kept");
        clock.Now = clock.Now + KeptFor - TimeSpan.FromHours(1);

        // Act
        int purged = await ServiceOf(provider).PurgeDeletedAsync();

        // Assert: other tests' bins, in the same store, may have been due too.
        Assert.True(purged >= 1);
        Assert.Equal([kept.VersionId], (await ListDeletedAsync(container)).Select(v => v.VersionId));
        ResourceCleanup cleanup = Assert.Single(RecordingCleanupHandler.In(container.Name));
        Assert.Equal((due.VersionId, ResourceCleanupReason.Purged), (cleanup.Resource.VersionId, cleanup.Reason));
    }

    [Fact]
    public async Task PurgeDeletedAsync_ShouldLeaveAVersion_WhenItWasUndeletedSinceItWasListed()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo version = await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.DeleteResourceIfExistsAsync("a");
        clock.Now += KeptFor + TimeSpan.FromMinutes(1);
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        ResourceInfo listed = Assert.Single(await repository.GetPurgeableAsync(clock.Now, 100), v => v.ContainerName == container.Name);
        await container.UndeleteAsync("a");

        // Act
        int purged = await repository.PurgeDeletedAsync(new ResourceLocation(container.Name, "a"), listed.VersionId, clock.Now);

        // Assert
        Assert.Equal(0, purged);
        Assert.Equal(version.VersionId, (await container.GetResourceInfoAsync("a"))!.VersionId);
    }

    [Fact]
    public async Task UploadAsync_ShouldPruneTheReplacedVersionIntoTheBin_WhenTheContainerKeepsDeletedVersions()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseVersioning(1).UseCleanupHandler<RecordingCleanupHandler>());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo replaced = await container.UploadAsync("a", BinaryData.FromString("old"));

        // Act
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("new"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert: in the bin at once, with no grace period, and reported as pruned.
        Assert.Equal([current.VersionId], (await container.GetResourceVersionsAsync("a")).Select(v => v.VersionId));
        ResourceInfo binned = Assert.Single(await ListDeletedAsync(container));
        Assert.Equal((replaced.VersionId, null), (binned.VersionId, binned.ExpiresAt));
        ResourceCleanup cleanup = Assert.Single(RecordingCleanupHandler.In(container.Name));
        Assert.Equal((replaced.VersionId, ResourceCleanupReason.Pruned), (cleanup.Resource.VersionId, cleanup.Reason));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteForGoodRatherThanKeep_WhenAVersionExpires()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), new ResourceUploadOptions { ExpiresAt = clock.Now.AddMinutes(1) });
        clock.Now = clock.Now.AddMinutes(2);

        // Act
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        Assert.False(await container.ExistsAsync("a"));
        Assert.Empty(await ListDeletedAsync(container));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldLeaveAVersionInTheBinAndExpireItOnceItsBack_WhenItsExpiryPassesInTheBin()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), new ResourceUploadOptions { ExpiresAt = clock.Now.AddMinutes(1) });
        await container.DeleteResourceIfExistsAsync("a");
        clock.Now = clock.Now.AddMinutes(2);

        // Act
        await ServiceOf(provider).DeleteExpiredAsync();
        bool keptInTheBin = (await ListDeletedAsync(container)).Count == 1;
        await container.UndeleteAsync("a");
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        Assert.True(keptInTheBin);
        Assert.False(await container.ExistsAsync("a"));
        Assert.Empty(await ListDeletedAsync(container));
    }

    [Fact]
    public async Task ListDeletedAsync_ShouldListNewestDeletionFirst_WhenGivenAPrefixAndSmallPages()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        foreach (string name in new[] { "x/1", "y/1", "x/2", "x/3" })
        {
            await container.UploadAsync(name, BinaryData.FromString(name));
            await container.DeleteResourceIfExistsAsync(name);
            clock.Now = clock.Now.AddSeconds(1);
        }

        // Act
        List<ResourceInfo> all = await ListDeletedAsync(container, pageSize: 1);
        List<ResourceInfo> matching = await ListDeletedAsync(container, "x/", pageSize: 2);

        // Assert
        Assert.Equal(["x/3", "x/2", "y/1", "x/1"], all.Select(v => v.Name));
        Assert.Equal(["x/3", "x/2", "x/1"], matching.Select(v => v.Name));
        ResourcePage first = await container.GetDeletedPageAsync(new ResourceListOptions { PageSize = 1 });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            container.GetDeletedPageAsync(new ResourceListOptions { Prefix = "x/" }, first.ContinuationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            container.GetDeletedPageAsync(new ResourceListOptions { Metadata = new ResourceMetadataMatch("k", "v") }));
    }

    [Fact]
    public async Task RenameResourceAsync_ShouldLeaveTheBinWithTheOldName_WhenTheNameHasVersionsInTheBin()
    {
        Requires(SupportsSoftDelete, "a bin");
        Requires(SupportsMove, "moves");

        // Arrange: "a" has one version in the bin and one current, and "b" has only one in the bin.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo binnedA = await container.UploadAsync("a", BinaryData.FromString("a1"));
        ResourceInfo currentA = await container.UploadAsync("a", BinaryData.FromString("a2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await container.DeleteResourceIfExistsAsync("a", binnedA.VersionId);
        ResourceInfo binnedB = await container.UploadAsync("b", BinaryData.FromString("b"));
        await container.DeleteResourceIfExistsAsync("b");

        // Act
        ResourceInfo? renamed = await container.RenameResourceAsync("a", "b");

        // Assert
        Assert.Equal(currentA.VersionId, renamed!.VersionId);
        Assert.Equal([currentA.VersionId], (await container.GetResourceVersionsAsync("b")).Select(v => v.VersionId));
        Assert.Equal([("a", binnedA.VersionId), ("b", binnedB.VersionId)],
            (await ListDeletedAsync(container)).Select(v => (v.Name, v.VersionId)).Order());
        Assert.True(await container.UndeleteAsync("a"));
        Assert.Equal(binnedA.VersionId, (await container.GetResourceInfoAsync("a"))!.VersionId);
    }

    [Fact]
    public async Task GetResourcePageAsync_ShouldLeaveOutANameWhoseCurrentVersionIsInTheBin_WhenSearchingByMetadata()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock, o => o.UseMetadataSearch("k"));
        IResourceContainerClient container = await NewContainerAsync(provider);
        var tagged = new ResourceUploadOptions { Metadata = new Dictionary<string, string> { ["k"] = "v" } };
        await container.UploadAsync("a", BinaryData.FromString("a"), tagged);
        await container.UploadAsync("b", BinaryData.FromString("b"), tagged);
        var search = new ResourceListOptions { Metadata = new ResourceMetadataMatch("k", "v") };

        // Act
        await container.DeleteResourceIfExistsAsync("a");
        List<string> whileDeleted = [.. (await container.GetResourcePageAsync(search)).Resources.Select(r => r.Name)];
        await container.UndeleteAsync("a");
        List<string> onceBack = [.. (await container.GetResourcePageAsync(search)).Resources.Select(r => r.Name)];

        // Assert
        Assert.Equal(["b"], whileDeleted);
        Assert.Equal(["a", "b"], onceBack);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldRefuseAndKeepTheVersionOutOfTheBin_WhenItsRetained()
    {
        Requires(SupportsSoftDelete, "a bin");
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), Retained(clock.Now.AddHours(1)));

        // Act & Assert
        await Assert.ThrowsAsync<ResourceRetainedException>(() => container.DeleteResourceIfExistsAsync("a"));
        Assert.True(await container.ExistsAsync("a"));
        Assert.Empty(await ListDeletedAsync(container));
        clock.Now = clock.Now.AddHours(2);
        await container.DeleteResourceIfExistsAsync("a");
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldDeleteTheBinToo_WhenTheContainerHasOne()
    {
        Requires(SupportsSoftDelete, "a bin");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildBinProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.DeleteResourceIfExistsAsync("a");

        // Act
        await ServiceOf(provider).DeleteContainerIfExistsAsync(container.Name);
        IResourceContainerClient again = await ServiceOf(provider).CreateContainerAsync(container.Name);

        // Assert
        Assert.Empty(await ListDeletedAsync(again));
        Assert.Equal(0L, (await again.GetUsageAsync()).DeletedVersionCount);
        await ServiceOf(provider).DeleteContainerIfExistsAsync(container.Name);
    }
}
