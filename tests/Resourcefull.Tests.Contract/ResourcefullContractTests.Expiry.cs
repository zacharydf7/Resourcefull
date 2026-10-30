using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

/// <remarks>
/// A sweep deletes expired versions in every container, so tests that share a store sweep each other's too: each checks
/// only its own containers, never the count a sweep returns.
/// </remarks>
public abstract partial class ResourcefullContractTests
{
    private static readonly DateTimeOffset Past = DateTimeOffset.UtcNow.AddDays(-1);
    private static readonly DateTimeOffset Future = DateTimeOffset.UtcNow.AddDays(1);

    /// <summary>Records what each test's containers clean up, and refuses the ones a test says to.</summary>
    private sealed class RecordingCleanupHandler : IResourceCleanupHandler
    {
        public static readonly ConcurrentQueue<ResourceCleanup> Cleanups = new();
        public static readonly ConcurrentDictionary<string, bool> Refusing = new();

        public ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken)
        {
            if (Refusing.ContainsKey(cleanup.Resource.ContainerName)) throw new InvalidOperationException("The audit system is down.");
            Cleanups.Enqueue(cleanup);
            return ValueTask.CompletedTask;
        }

        public static List<ResourceCleanup> In(string containerName) => [.. Cleanups.Where(c => c.Resource.ContainerName == containerName)];
    }

    private static ResourceUploadOptions Expiring(DateTimeOffset expiresAt) => new() { ExpiresAt = expiresAt };

    [SkippableFact]
    public async Task DeleteExpiredAsync_ShouldDeleteOnlyExpiredVersions_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("expired", BinaryData.FromString("x"), Expiring(Past));
        ResourceInfo later = await container.UploadAsync("later", BinaryData.FromString("x"), Expiring(Future));
        await container.UploadAsync("permanent", BinaryData.FromString("x"));

        // Act
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        Assert.Equal(["later", "permanent"], (await container.GetResourcesAsync()).Select(r => r.Name).Order());
        Assert.Equal(later.ExpiresAt, (await container.GetResourceInfoAsync("later"))!.ExpiresAt);
        Assert.Null((await container.GetResourceInfoAsync("permanent"))!.ExpiresAt);
    }

    [SkippableFact]
    public async Task DeleteExpiredAsync_ShouldMakeThePreviousVersionCurrent_WhenTheCurrentVersionExpires()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo lasting = await container.UploadAsync("notes.txt", BinaryData.FromString("lasting"));
        await container.UploadAsync("notes.txt", BinaryData.FromString("temporary"), Expiring(Past));

        // Act
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        ResourceInfo current = (await container.GetResourceInfoAsync("notes.txt"))!;
        Assert.Equal((lasting.VersionId, true), (current.VersionId, current.IsLatest));
        Assert.Single(await container.GetResourceVersionsAsync("notes.txt"));
    }

    [SkippableFact]
    public async Task SetExpiryAsync_ShouldChangeOrClearTheExpiry_WhenTheVersionExists()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo extended = await container.UploadAsync("extended", BinaryData.FromString("x"), Expiring(Past));
        ResourceInfo cleared = await container.UploadAsync("cleared", BinaryData.FromString("x"), Expiring(Past));
        ResourceInfo shortened = await container.UploadAsync("shortened", BinaryData.FromString("x"));

        // Act
        bool[] found =
        [
            await container.SetExpiryAsync("extended", extended.VersionId, Future),
            await container.SetExpiryAsync("cleared", cleared.VersionId, null),
            await container.SetExpiryAsync("shortened", shortened.VersionId, Past),
            await container.SetExpiryAsync("missing", "no-such-version", Future)
        ];
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        Assert.Equal([true, true, true, false], found);
        Assert.Equal(["cleared", "extended"], (await container.GetResourcesAsync()).Select(r => r.Name).Order());
        Assert.Equal(Future, (await container.GetResourceInfoAsync("extended"))!.ExpiresAt);
        Assert.Equal(extended.LastModified, (await container.GetResourceInfoAsync("extended"))!.LastModified);
    }

    [SkippableFact]
    public async Task CopyMoveAndRestore_ShouldSetTheExpiryOnlyWhenAsked_WhenTheSourceExpires()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        string name = container.ResourceContainerInfo.Name;
        ResourceInfo first = await container.UploadAsync("a", BinaryData.FromString("1"), Expiring(Future));
        await container.UploadAsync("a", BinaryData.FromString("2"));

        // Act
        ResourceInfo? plainCopy = await container.CopyResourceAsync("a", name, "copy", new ResourceCopyOptions { VersionId = first.VersionId });
        ResourceInfo? expiringCopy = await container.CopyResourceAsync("a", name, "expiring", new ResourceCopyOptions { ExpiresAt = Past });
        ResourceInfo? restored = await container.RestoreVersionAsync("a", first.VersionId);
        await container.MoveResourceAsync("a", name, "moved");

        // Assert
        Assert.Null(plainCopy!.ExpiresAt);
        Assert.Equal(Past, expiringCopy!.ExpiresAt);
        Assert.Null(restored!.ExpiresAt);
        Assert.Equal(first.ExpiresAt, (await container.GetResourceInfoAsync("moved", first.VersionId))!.ExpiresAt);
    }

    [SkippableFact]
    public async Task CleanupHandler_ShouldHearOfEveryExpiredAndPrunedVersionBeforeItIsDeleted_WhenOneIsRegistered()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(1).UseCleanupHandler<RecordingCleanupHandler>());
        IResourceContainerClient container = await NewContainerAsync(provider);
        string name = container.ResourceContainerInfo.Name;
        ResourceInfo expired = await container.UploadAsync("temporary", BinaryData.FromString("x"), Expiring(Past));
        ResourceInfo pruned = await container.UploadAsync("notes.txt", BinaryData.FromString("1"));

        // Act
        await container.UploadAsync("notes.txt", BinaryData.FromString("2"));
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        List<ResourceCleanup> cleanups = RecordingCleanupHandler.In(name);
        Assert.Contains(cleanups, c => c.Reason == ResourceCleanupReason.Pruned && c.Resource.VersionId == pruned.VersionId);
        Assert.Contains(cleanups, c => c.Reason == ResourceCleanupReason.Expired && c.Resource.VersionId == expired.VersionId);
        Assert.Equal(2, cleanups.Count);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldKeepAPrunedVersionUntilItsGracePeriodEnds_WhenExpiryIsOn()
    {
        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(o =>
        {
            o.UseVersioning(1).UseExpiry().UseCleanupHandler<RecordingCleanupHandler>();
            o.Services.AddSingleton<TimeProvider>(clock);
        });
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("notes.txt", BinaryData.FromString("1"));
        DateTimeOffset pruned = clock.Now;

        // Act
        await container.UploadAsync("notes.txt", BinaryData.FromString("2"));
        ResourceInfo? kept = await container.GetResourceInfoAsync("notes.txt", first.VersionId);
        bool reportedWhilePruning = RecordingCleanupHandler.In(container.ResourceContainerInfo.Name).Count > 0;
        clock.Now = clock.Now.AddMinutes(6);
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert: kept, expiring five minutes after the prune, then deleted by the sweep, which reports it.
        Assert.Equal(pruned.AddMinutes(5), kept!.ExpiresAt);
        Assert.False(reportedWhilePruning);
        Assert.False(await container.ExistsAsync("notes.txt", first.VersionId));
        ResourceCleanup cleanup = Assert.Single(RecordingCleanupHandler.In(container.ResourceContainerInfo.Name));
        Assert.Equal((ResourceCleanupReason.Expired, first.VersionId), (cleanup.Reason, cleanup.Resource.VersionId));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldKeepAPrunedVersionsSoonerExpiry_WhenExpiryIsOn()
    {
        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(o =>
        {
            o.UseVersioning(1).UseExpiry();
            o.Services.AddSingleton<TimeProvider>(clock);
        });
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("notes.txt", BinaryData.FromString("1"), Expiring(clock.Now.AddMinutes(1)));

        // Act
        await container.UploadAsync("notes.txt", BinaryData.FromString("2"));

        // Assert
        Assert.Equal(clock.Now.AddMinutes(1), (await container.GetResourceInfoAsync("notes.txt", first.VersionId))!.ExpiresAt);
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldDeleteTheReplacedVersionsToo_WhenTheCurrentVersionIsDeletedWithoutVersioning()
    {
        // Arrange: without versioning, the version an overwrite replaced waits out its grace period.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseExpiry());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("notes.txt", BinaryData.FromString("replaced"));
        ResourceInfo current = await container.UploadAsync("notes.txt", BinaryData.FromString("current"),
            new ResourceUploadOptions { Overwrite = true });

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("notes.txt", current.VersionId);

        // Assert: the replaced content didn't become current again.
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("notes.txt"));
        Assert.Empty(await container.GetResourceVersionsAsync("notes.txt"));
    }

    [SkippableFact]
    public async Task DeleteExpiredAsync_ShouldDeleteTheReplacedVersionsToo_WhenTheCurrentVersionExpiresWithoutVersioning()
    {
        // Arrange: an overwrite that expires sooner than the grace period of the version it replaced.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(o =>
        {
            o.UseExpiry();
            o.Services.AddSingleton<TimeProvider>(clock);
        });
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("notes.txt", BinaryData.FromString("replaced"));
        await container.UploadAsync("notes.txt", BinaryData.FromString("current"),
            new ResourceUploadOptions { Overwrite = true, ExpiresAt = clock.Now.AddMinutes(1) });

        // Act
        clock.Now = clock.Now.AddMinutes(2);
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        Assert.False(await container.ExistsAsync("notes.txt"));
        Assert.Empty(await container.GetResourceVersionsAsync("notes.txt"));
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldFinishReading_WhenAnUploadReplacesTheVersionWhileExpiryIsOn()
    {
        // Arrange: without versioning an upload replaces the version before it; the content spans several pages.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseExpiry());
        IResourceContainerClient container = await NewContainerAsync(provider);
        byte[] content = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        await container.UploadAsync("video.mp4", new MemoryStream(content));
        await using ResourceDownloadResult download = (await container.DownloadStreamingAsync("video.mp4"))!;
        byte[] start = new byte[1000];
        await download.Stream.ReadExactlyAsync(start);

        // Act
        await container.UploadAsync("video.mp4", BinaryData.FromString("replaced"), new ResourceUploadOptions { Overwrite = true });
        var rest = new MemoryStream();
        await download.Stream.CopyToAsync(rest);

        // Assert
        Assert.Equal(content, start.Concat(rest.ToArray()).ToArray());
    }

    [SkippableFact]
    public async Task CleanupHandler_ShouldKeepTheVersionButNotHoldUpOthers_WhenTheHandlerFailsForIt()
    {
        // Arrange: the handler refuses one container's versions; another container's expire later.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(1).UseCleanupHandler<RecordingCleanupHandler>());
        IResourceContainerClient container = await NewContainerAsync(provider);
        IResourceContainerClient other = await NewContainerAsync(provider);
        string name = container.ResourceContainerInfo.Name;
        await container.UploadAsync("temporary", BinaryData.FromString("x"), Expiring(Past));
        await other.UploadAsync("temporary", BinaryData.FromString("x"), Expiring(Past.AddMinutes(1)));
        await container.UploadAsync("notes.txt", BinaryData.FromString("1"));
        RecordingCleanupHandler.Refusing[name] = true;

        try
        {
            // Act
            await container.UploadAsync("notes.txt", BinaryData.FromString("2"));
            Exception? sweep = await Record.ExceptionAsync(() => ServiceOf(provider).DeleteExpiredAsync());

            // Assert: the upload still succeeded, nothing was deleted unreported, and the other container's version went.
            Assert.IsType<InvalidOperationException>(Assert.Single(Assert.IsType<AggregateException>(sweep).InnerExceptions));
            Assert.True(await container.ExistsAsync("temporary"));
            Assert.False(await other.ExistsAsync("temporary"));
            Assert.Equal(2, (await container.GetResourceVersionsAsync("notes.txt")).Count);
        }
        finally
        {
            // The expired version stays in a shared store; other tests' sweeps mustn't meet a refusing handler for it.
            RecordingCleanupHandler.Refusing.TryRemove(name, out _);
            await container.DeleteResourceIfExistsAsync("temporary");
        }
    }

    [SkippableFact]
    public async Task DeleteExpiredVersionAsync_ShouldKeepTheVersion_WhenItsExpiryWasMovedPastTheSweepsTime()
    {
        // Arrange: listed as expired by a sweep, then extended before the sweep deleted it.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo stored = await container.UploadAsync("a", BinaryData.FromString("x"), Expiring(Past));
        DateTimeOffset sweepTime = DateTimeOffset.UtcNow;
        await container.SetExpiryAsync("a", stored.VersionId, Future);
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();

        // Act
        bool deleted = await repository.DeleteExpiredVersionAsync(new ResourceLocation(container.ResourceContainerInfo.Name, "a"),
            stored.VersionId, sweepTime);

        // Assert
        Assert.False(deleted);
        Assert.True(await container.ExistsAsync("a"));
    }

    [SkippableFact]
    public async Task DeleteExpiredAsync_ShouldUseTheRegisteredClock_WhenOneIsRegistered()
    {
        // Arrange: an application test's clock, far from the real one.
        var clock = new ManualTimeProvider { Now = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        using ServiceProvider provider = BuildServiceProvider(o => o.Services.AddSingleton<TimeProvider>(clock));
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("x"), Expiring(clock.Now.AddHours(1)));
        IResourceServiceClient service = ServiceOf(provider);

        // Act
        await service.DeleteExpiredAsync();
        bool keptBefore = await container.ExistsAsync("a");
        clock.Now = clock.Now.AddHours(2);
        await service.DeleteExpiredAsync();

        // Assert
        Assert.True(keptBefore);
        Assert.False(await container.ExistsAsync("a"));
    }

    [SkippableFact]
    public async Task DeleteExpiredAsync_ShouldKeepAReadOnlyContainersVersionsButNotHoldUpOthers_WhenTheyExpireFirst()
    {
        // Arrange: a frozen container whose version expires before another container's.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string frozenName = NewContainerName();
        IResourceContainerClient frozen = await service.CreateResourceContainerAsync(frozenName);
        IResourceContainerClient other = await NewContainerAsync(provider);
        await frozen.UploadAsync("a", BinaryData.FromString("x"), Expiring(Past));
        await other.UploadAsync("a", BinaryData.FromString("x"), Expiring(Past.AddMinutes(1)));
        TestPolicyProvider.Policies[frozenName] = new ResourceContainerPolicy { ReadOnly = true };

        // Act
        await service.DeleteExpiredAsync();
        bool keptWhileFrozen = await frozen.ExistsAsync("a");
        TestPolicyProvider.Policies[frozenName] = new ResourceContainerPolicy { ReadOnly = false };
        await service.DeleteExpiredAsync();

        // Assert
        Assert.True(keptWhileFrozen);
        Assert.False(await other.ExistsAsync("a"));
        Assert.False(await frozen.ExistsAsync("a"));
    }

    [SkippableFact]
    public async Task GetExpiredAsync_ShouldListEachExpiredVersionOnceInOrder_WhenPagedOneAtATimeFromTheLast()
    {
        // Arrange: expiries long before any other test's, two of them equal, so the version ID breaks the tie.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        var tied = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ResourceInfo[] stored =
        [
            await container.UploadAsync("a", BinaryData.FromString("x"), Expiring(tied)),
            await container.UploadAsync("b", BinaryData.FromString("x"), Expiring(tied)),
            await container.UploadAsync("c", BinaryData.FromString("x"), Expiring(tied.AddHours(1)))
        ];
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        DateTimeOffset now = tied.AddDays(1);

        // Act
        var listed = new List<ResourceInfo>();
        ResourceInfo? after = null;
        for (int page = 0; page < 10; page++)
        {
            List<ResourceInfo> batch = await repository.GetExpiredAsync(now, 1, after);
            if (batch.Count == 0) break;
            listed.Add(after = Assert.Single(batch));
        }

        // Assert
        Assert.Equal(stored.OrderBy(v => v.ExpiresAt).ThenBy(v => v.VersionId, StringComparer.Ordinal).Select(v => v.VersionId),
            listed.Select(v => v.VersionId));
        await ServiceOf(provider).DeleteExpiredAsync();
    }

    [SkippableFact]
    public async Task SetExpiryAsync_ShouldBeRefused_WhenTheContainerIsReadOnly()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        string name = NewContainerName();
        IResourceContainerClient container = await ServiceOf(provider).CreateResourceContainerAsync(name);
        ResourceInfo stored = await container.UploadAsync("a", BinaryData.FromString("x"));
        TestPolicyProvider.Policies[name] = new ResourceContainerPolicy { ReadOnly = true };

        // Act
        Task act() => container.SetExpiryAsync("a", stored.VersionId, Future);

        // Assert
        Assert.Equal(ResourcePolicyRule.ReadOnly, (await Assert.ThrowsAsync<ResourcePolicyViolationException>(act)).Rule);
        Assert.Null((await container.GetResourceInfoAsync("a"))!.ExpiresAt);
    }
}
