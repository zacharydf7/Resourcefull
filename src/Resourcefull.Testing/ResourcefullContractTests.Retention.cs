using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

/// <remarks>
/// Retained and held versions can't be deleted, so these tests retain versions until a time a clock of their own
/// passes, or lift their holds, where a version would otherwise stay in the shared store for good.
/// </remarks>
public abstract partial class ResourcefullContractTests
{
    /// <summary>A provider on a clock the test moves, starting now.</summary>
    private ServiceProvider BuildServiceProvider(ManualTimeProvider clock, Action<ResourcefullOptionsBuilder>? configure = null) =>
        BuildServiceProvider(o =>
        {
            configure?.Invoke(o);
            o.Services.AddSingleton<TimeProvider>(clock);
        });

    private static ResourceUploadOptions Retained(DateTimeOffset retainUntil) =>
        new() { Mode = ResourceWriteMode.Replace, RetainUntil = retainUntil };

    [Fact]
    public async Task UploadAsync_ShouldStoreTheRetentionAndHold_WhenTheOptionsSetThem()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        DateTimeOffset retainUntil = clock.Now.AddHours(1);

        // Act
        ResourceInfo uploaded = await container.UploadAsync("a", BinaryData.FromString("a"),
            new ResourceUploadOptions { RetainUntil = retainUntil, LegalHold = true });

        // Assert
        ResourceInfo stored = (await container.GetResourceInfoAsync("a"))!;
        Assert.Equal((retainUntil, true), (uploaded.RetainUntil, uploaded.LegalHold));
        Assert.Equal((Kept(retainUntil), true), (stored.RetainUntil, stored.LegalHold));
        await container.SetLegalHoldAsync("a", null, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteResourceIfExistsAsync_ShouldRefuseAndDeleteNothing_WhenAVersionItWouldDeleteIsRetained(bool wholeName)
    {
        Requires(SupportsRetention, "retention");

        // Arrange: the name's older version isn't retained, and its current one is.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock, o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo older = await container.UploadAsync("a", BinaryData.FromString("older"));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("current"), Retained(clock.Now.AddHours(1)));

        // Act
        ResourceRetainedException refused = await Assert.ThrowsAsync<ResourceRetainedException>(() =>
            container.DeleteResourceIfExistsAsync("a", wholeName ? null : current.VersionId));

        // Assert
        Assert.Equal((current.VersionId, Kept(clock.Now.AddHours(1)), false), (refused.VersionId, refused.RetainUntil, refused.LegalHold));
        Assert.Equal((container.Name, "a"), (refused.ContainerName, refused.ResourceName));
        Assert.Equal([current.VersionId, older.VersionId], (await container.GetResourceVersionsAsync("a")).Select(v => v.VersionId));
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldDeleteTheVersion_WhenItsRetentionHasPassed()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), Retained(clock.Now.AddHours(1)));
        clock.Now = clock.Now.AddHours(2);

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("a");

        // Assert
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldRefuseUntilTheHoldIsLifted_WhenTheVersionIsUnderALegalHold()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo version = await container.UploadAsync("a", BinaryData.FromString("a"));
        bool held = await container.SetLegalHoldAsync("a", version.VersionId, true);

        // Act
        ResourceRetainedException refused = await Assert.ThrowsAsync<ResourceRetainedException>(() => container.DeleteResourceIfExistsAsync("a"));
        bool lifted = await container.SetLegalHoldAsync("a", null, false);
        bool deleted = await container.DeleteResourceIfExistsAsync("a");

        // Assert
        Assert.True(held);
        Assert.Equal((version.VersionId, null, true), (refused.VersionId, refused.RetainUntil, refused.LegalHold));
        Assert.True(lifted);
        Assert.True(deleted);
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldRefuseAndDeleteNothing_WhenAVersionInItIsRetained()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));
        ResourceInfo retained = await container.UploadAsync("b", BinaryData.FromString("b"), Retained(clock.Now.AddHours(1)));

        // Act
        ResourceRetainedException refused = await Assert.ThrowsAsync<ResourceRetainedException>(() =>
            ServiceOf(provider).DeleteContainerIfExistsAsync(container.Name));
        List<ResourceInfo> left = await container.ListResourcesAsync().ToListAsync();
        clock.Now = clock.Now.AddHours(2);
        bool deletedLater = await ServiceOf(provider).DeleteContainerIfExistsAsync(container.Name);

        // Assert: refused, with both resources left, until the retention passed.
        Assert.Equal(retained.VersionId, refused.VersionId);
        Assert.Equal(["a", "b"], left.Select(r => r.Name));
        Assert.True(deletedLater);
        Assert.False(await ServiceOf(provider).ContainerExistsAsync(container.Name));
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldLeaveEveryResource_WhenAVersionInItIsHeld()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.UploadAsync("b", BinaryData.FromString("b"));
        await container.SetLegalHoldAsync("b", null, true);

        // Act
        await Assert.ThrowsAsync<ResourceRetainedException>(() => ServiceOf(provider).DeleteContainerIfExistsAsync(container.Name));

        // Assert
        Assert.Equal(["a", "b"], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
        await container.SetLegalHoldAsync("b", null, false);
    }

    [Fact]
    public async Task SetRetentionAsync_ShouldMoveTheDateLaterButNeverEarlier_WhenGivenDates()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), Retained(clock.Now.AddHours(1)));

        // Act
        bool later = await container.SetRetentionAsync("a", null, clock.Now.AddHours(2));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => container.SetRetentionAsync("a", null, clock.Now.AddMinutes(30)));
        bool missing = await container.SetRetentionAsync("b", null, clock.Now.AddHours(1));

        // Assert
        Assert.True(later);
        Assert.False(missing);
        Assert.Equal(Kept(clock.Now.AddHours(2)), (await container.GetResourceInfoAsync("a"))!.RetainUntil);
    }

    [Fact]
    public async Task SetRetentionAsync_ShouldChangeOnlyTheVersionGiven_WhenGivenAVersionId()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock, o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo older = await container.UploadAsync("a", BinaryData.FromString("older"));
        await container.UploadAsync("a", BinaryData.FromString("current"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        await container.SetRetentionAsync("a", older.VersionId, clock.Now.AddHours(1));
        await container.SetLegalHoldAsync("a", older.VersionId, true);

        // Assert
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("a");
        Assert.Equal((null, false), (versions[0].RetainUntil, versions[0].LegalHold));
        Assert.Equal((Kept(clock.Now.AddHours(1)), true), (versions[1].RetainUntil, versions[1].LegalHold));
        Assert.False(await container.SetLegalHoldAsync("a", "missing", true));
        await container.SetLegalHoldAsync("a", older.VersionId, false);
    }

    [Fact]
    public async Task UploadAsync_ShouldKeepARetainedVersionPastTheLimitUntilItsRetentionPasses_WhenItsReplaced()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock, o => o.UseVersioning(1));
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo retained = await container.UploadAsync("a", BinaryData.FromString("1"), Retained(clock.Now.AddHours(1)));

        // Act
        ResourceInfo second = await container.UploadAsync("a", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        IReadOnlyList<ResourceInfo> whileRetained = await container.GetResourceVersionsAsync("a");
        clock.Now = clock.Now.AddHours(2);
        ResourceInfo third = await container.UploadAsync("a", BinaryData.FromString("3"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert: the replaced version that isn't retained goes, the retained one stays until the next prune after its date.
        Assert.Equal([second.VersionId, retained.VersionId], whileRetained.Select(v => v.VersionId));
        Assert.Equal([third.VersionId], (await container.GetResourceVersionsAsync("a")).Select(v => v.VersionId));
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldRefuseToDeleteTheCurrentVersion_WhenItReplacedARetainedOneInAContainerThatKeepsOne()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock, o => o.UseVersioning(1));
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo retained = await container.UploadAsync("a", BinaryData.FromString("1"), Retained(clock.Now.AddHours(1)));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        ResourceRetainedException refused = await Assert.ThrowsAsync<ResourceRetainedException>(() =>
            container.DeleteResourceIfExistsAsync("a", current.VersionId));

        // Assert: deleting it would make the replaced version current again, so both stay.
        Assert.Equal(retained.VersionId, refused.VersionId);
        Assert.Equal([current.VersionId, retained.VersionId], (await container.GetResourceVersionsAsync("a")).Select(v => v.VersionId));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldKeepAnExpiredVersionUntilItsRetentionPasses_WhenItsRetained()
    {
        Requires(SupportsExpiry, "expiry");
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock, o => o.UseExpiry());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"),
            new ResourceUploadOptions { ExpiresAt = clock.Now.AddMinutes(-1), RetainUntil = clock.Now.AddHours(1) });

        // Act
        await ServiceOf(provider).DeleteExpiredAsync();
        bool keptWhileRetained = await container.ExistsAsync("a");
        IReadOnlyList<ResourceInfo> listedWhileRetained = await ServiceOf(provider).GetExpiredVersionsAsync(1000);
        clock.Now = clock.Now.AddHours(2);
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        Assert.True(keptWhileRetained);
        Assert.DoesNotContain(listedWhileRetained, v => v.ContainerName == container.Name);
        Assert.False(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task UploadAsync_ShouldRetainTheVersionForTheContainersRetainFor_WhenTheUploadSetsNoDate()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock,
            o => o.UseContainerPolicies(new ResourceContainerPolicy { RetainFor = TimeSpan.FromHours(1) }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        DateTimeOffset ownDate = clock.Now.AddMinutes(10);

        // Act
        ResourceInfo byPolicy = await container.UploadAsync("a", BinaryData.FromString("a"));
        ResourceInfo byUpload = await container.UploadAsync("b", BinaryData.FromString("b"), new ResourceUploadOptions { RetainUntil = ownDate });
        ResourceInfo? restored = await container.RestoreVersionAsync("b", byUpload.VersionId);

        // Assert: the policy counts from the write; a write's own date wins; a restore takes the policy's, not the source's.
        Assert.Equal(Kept(clock.Now.AddHours(1)), (await container.GetResourceInfoAsync("a", byPolicy.VersionId))!.RetainUntil);
        Assert.Equal(Kept(ownDate), (await container.GetResourceInfoAsync("b", byUpload.VersionId))!.RetainUntil);
        Assert.Equal(Kept(clock.Now.AddHours(1)), (await container.GetResourceInfoAsync("b", restored!.VersionId))!.RetainUntil);
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldTakeTheCopysOwnRetentionAndHold_WhenCopyingARetainedVersion()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"),
            new ResourceUploadOptions { RetainUntil = clock.Now.AddHours(1), LegalHold = true });

        // Act
        await container.CopyResourceAsync("a", container.Name, "plain");
        await container.CopyResourceAsync("a", container.Name, "own", new ResourceCopyOptions { RetainUntil = clock.Now.AddHours(2) });

        // Assert: the source's aren't copied.
        ResourceInfo plain = (await container.GetResourceInfoAsync("plain"))!;
        ResourceInfo own = (await container.GetResourceInfoAsync("own"))!;
        Assert.Equal((null, false), (plain.RetainUntil, plain.LegalHold));
        Assert.Equal((Kept(clock.Now.AddHours(2)), false), (own.RetainUntil, own.LegalHold));
        await container.SetLegalHoldAsync("a", null, false);
    }

    [Fact]
    public async Task RenameResourceAsync_ShouldTakeTheRetentionAndHoldWithTheVersion_WhenTheVersionIsRetained()
    {
        Requires(SupportsMove, "moves");
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"),
            new ResourceUploadOptions { RetainUntil = clock.Now.AddHours(1), LegalHold = true });

        // Act
        ResourceInfo? renamed = await container.RenameResourceAsync("a", "b");

        // Assert
        Assert.Equal((Kept(clock.Now.AddHours(1)), true), (renamed!.RetainUntil, renamed.LegalHold));
        await Assert.ThrowsAsync<ResourceRetainedException>(() => container.DeleteResourceIfExistsAsync("b"));
        await container.SetLegalHoldAsync("b", null, false);
    }

    [Fact]
    public async Task CompleteUploadAsync_ShouldStoreTheSessionsRetentionAndHold_WhenTheSessionSetThem()
    {
        Requires(SupportsUploadSessions, "upload sessions");
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload upload = await container.StartUploadAsync("a",
            new ResourceUploadSessionOptions { RetainUntil = clock.Now.AddHours(1), LegalHold = true });
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream([1, 2, 3]));

        // Act
        ResourceInfo? completed = await container.CompleteUploadAsync("a", upload.UploadId);

        // Assert
        ResourceInfo stored = (await container.GetResourceInfoAsync("a"))!;
        Assert.Equal((Kept(clock.Now.AddHours(1)), true), (stored.RetainUntil, stored.LegalHold));
        Assert.Equal(stored.VersionId, completed!.VersionId);
        await container.SetLegalHoldAsync("a", null, false);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldRestoreTheRetentionAndHolds_WhenTheExportHasThem()
    {
        Requires(SupportsImport, "import");
        Requires(SupportsRetention, "retention");

        // Arrange
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(clock);
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("retained", BinaryData.FromString("a"), new ResourceUploadOptions { RetainUntil = clock.Now.AddHours(1) });
        await container.UploadAsync("held", BinaryData.FromString("b"), new ResourceUploadOptions { LegalHold = true });
        await container.UploadAsync("plain", BinaryData.FromString("c"));
        using var export = new MemoryStream();
        await ServiceOf(provider).ExportContainerAsync(container.Name, export);
        export.Position = 0;
        string restoredName = NewContainerName();

        // Act
        await ServiceOf(provider).ImportContainerAsync(restoredName, export);

        // Assert
        IResourceContainerClient restored = ServiceOf(provider).GetContainerClient(restoredName);
        Assert.Equal((Kept(clock.Now.AddHours(1)), false), ((await restored.GetResourceInfoAsync("retained"))!.RetainUntil,
            (await restored.GetResourceInfoAsync("retained"))!.LegalHold));
        Assert.True((await restored.GetResourceInfoAsync("held"))!.LegalHold);
        Assert.Equal((null, false), ((await restored.GetResourceInfoAsync("plain"))!.RetainUntil, (await restored.GetResourceInfoAsync("plain"))!.LegalHold));
        await container.SetLegalHoldAsync("held", null, false);
        await restored.SetLegalHoldAsync("held", null, false);
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldNeverDeleteAHeldVersion_WhenAHoldIsSetAtTheSameTime()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();

        for (int i = 0; i < 5; i++)
        {
            IResourceContainerClient container = await NewContainerAsync(provider);
            await container.UploadAsync("a", BinaryData.FromString("a"));

            // Act: a hold on the container's only version, and a delete of the container, at once.
            Task<bool> hold = Task.Run(async () =>
            {
                try
                {
                    return await container.SetLegalHoldAsync("a", null, true);
                }
                catch (ResourceContainerNotFoundException)
                {
                    return false;
                }
            });
            Task<bool> delete = Task.Run(async () =>
            {
                try
                {
                    return await ServiceOf(provider).DeleteContainerIfExistsAsync(container.Name);
                }
                catch (ResourceRetainedException)
                {
                    return false;
                }
            });
            await Task.WhenAll(hold, delete);

            // Assert: either the delete went first, and the hold failed, or the hold did, and the container stays.
            bool exists = await container.ExistsAsync("a");
            Assert.True(delete.Result ^ hold.Result, $"Round {i}: deleted {delete.Result}, held {hold.Result}.");
            Assert.Equal(hold.Result, exists);
            if (exists) await container.SetLegalHoldAsync("a", null, false);
        }
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldNeverDeleteAHeldVersion_WhenOneIsUploadedAtTheSameTime()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();

        for (int i = 0; i < 5; i++)
        {
            IResourceContainerClient container = await NewContainerAsync(provider);

            // Act: an upload of a held version into the container, and a delete of the container, at once.
            Task<bool> upload = Task.Run(async () =>
            {
                try
                {
                    await container.UploadAsync("a", BinaryData.FromString("a"), new ResourceUploadOptions { LegalHold = true });
                    return true;
                }
                catch (ResourcefullException exception)
                    when (exception is ResourceContainerNotFoundException or ResourceUploadInterruptedException)
                {
                    // Overtaken by the delete: a provider that stages content first finds its version deleted.
                    return false;
                }
            });
            Task<bool> delete = Task.Run(async () =>
            {
                try
                {
                    return await ServiceOf(provider).DeleteContainerIfExistsAsync(container.Name);
                }
                catch (ResourceRetainedException)
                {
                    return false;
                }
            });
            await Task.WhenAll(upload, delete);

            // Assert: a held version that was stored is never deleted with its container.
            bool exists = await container.ExistsAsync("a");
            Assert.Equal(upload.Result, exists);
            Assert.True(upload.Result ^ delete.Result, $"Round {i}: uploaded {upload.Result}, deleted {delete.Result}.");
            if (exists) await container.SetLegalHoldAsync("a", null, false);
        }
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldNeverDeleteAHeldVersion_WhenAHoldIsSetAtTheSameTime()
    {
        Requires(SupportsRetention, "retention");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        for (int i = 0; i < 10; i++)
        {
            string name = $"r{i}";
            await container.UploadAsync(name, BinaryData.FromString("a"));

            // Act: a hold and a delete of the same version at once.
            Task<bool> hold = Task.Run(() => container.SetLegalHoldAsync(name, null, true));
            Task<bool> delete = Task.Run(async () =>
            {
                try
                {
                    return await container.DeleteResourceIfExistsAsync(name);
                }
                catch (ResourceRetainedException)
                {
                    return false;
                }
            });
            await Task.WhenAll(hold, delete);

            // Assert: either the delete went first, and the hold found nothing, or the hold did, and the version stays.
            bool exists = await container.ExistsAsync(name);
            Assert.True(delete.Result ^ hold.Result, $"Round {i}: deleted {delete.Result}, held {hold.Result}.");
            Assert.Equal(hold.Result, exists);
            if (exists) await container.SetLegalHoldAsync(name, null, false);
        }
    }
}
