using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Resourcefull.InMemory;
using Resourcefull.PostgreSql;
using Resourcefull.Providers;
using Resourcefull.Sqlite;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Unit;

public class ExpiryTests
{
    private static ResourceRepository RepositoryWithExpiry()
    {
        var repository = new Mock<ResourceRepository>();
        repository.SetupGet(r => r.SupportsExpiry).Returns(true);
        return repository.Object;
    }

    /// <summary>The expiry service, over a service client that answers each sweep with <paramref name="sweep"/>.</summary>
    private static (ExpiryService Service, Mock<IResourceServiceClient> Client) NewService(Func<int, Task<long>> sweep, TimeSpan interval)
    {
        var client = new Mock<IResourceServiceClient>();
        int calls = 0;
        client.Setup(c => c.DeleteExpiredAsync(It.IsAny<CancellationToken>())).Returns(() => sweep(++calls));
        ServiceProvider services = new ServiceCollection().AddScoped(_ => client.Object).AddScoped(_ => RepositoryWithExpiry())
            .BuildServiceProvider();
        return (new ExpiryService(services.GetRequiredService<IServiceScopeFactory>(), new ExpirySettings(interval), null,
            NullLogger<ExpiryService>.Instance), client);
    }

    [Fact]
    public async Task ExpiryService_ShouldSweepItsOwnStore_WhenTheStoreHasAName()
    {
        // Arrange: the store without a name, and the archive, each with a client that records its sweeps.
        var unnamed = new Mock<IResourceServiceClient>();
        var archive = new Mock<IResourceServiceClient>();
        var swept = new TaskCompletionSource();
        archive.Setup(c => c.DeleteExpiredAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            swept.TrySetResult();
            return Task.FromResult(0L);
        });
        ServiceProvider services = new ServiceCollection().AddScoped(_ => unnamed.Object)
            .AddKeyedScoped("archive", (_, _) => archive.Object).AddKeyedScoped("archive", (_, _) => RepositoryWithExpiry())
            .BuildServiceProvider();
        var service = new ExpiryService(services.GetRequiredService<IServiceScopeFactory>(), new ExpirySettings(TimeSpan.FromHours(1)),
            "archive", NullLogger<ExpiryService>.Instance);

        // Act
        await service.StartAsync(CancellationToken.None);
        Task reached = await Task.WhenAny(swept.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Same(swept.Task, reached);
        unnamed.Verify(c => c.DeleteExpiredAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpiryService_ShouldSweepAtStartAndKeepGoingAfterAFailure_WhenRunning()
    {
        // Arrange: the first sweep fails, as when storage can't be reached.
        var secondSweep = new TaskCompletionSource();
        (ExpiryService service, _) = NewService(call =>
        {
            if (call == 1) throw new InvalidOperationException("Storage is down.");
            secondSweep.TrySetResult();
            return Task.FromResult(0L);
        }, TimeSpan.FromMilliseconds(20));

        // Act
        await service.StartAsync(CancellationToken.None);
        Task reached = await Task.WhenAny(secondSweep.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Same(secondSweep.Task, reached);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryService_ShouldPurgeTheBinEverySweep_WhenDeletingExpiredVersionsSucceedsOrFails(bool expiryFails)
    {
        // Arrange: the first purge fails, and the sweep goes on to a second.
        (ExpiryService service, Mock<IResourceServiceClient> client) = NewService(
            _ => expiryFails ? throw new InvalidOperationException("Storage is down.") : Task.FromResult(0L), TimeSpan.FromMilliseconds(20));
        var secondPurge = new TaskCompletionSource();
        int purges = 0;
        client.Setup(c => c.PurgeDueAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (++purges == 1) throw new InvalidOperationException("Storage is down.");
            secondPurge.TrySetResult();
            return Task.FromResult(0L);
        });

        // Act
        await service.StartAsync(CancellationToken.None);
        Task reached = await Task.WhenAny(secondPurge.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Same(secondPurge.Task, reached);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExpiryService_ShouldKeepSweepingAndPurging_WhenAProviderThatClaimsExpiryThrowsNotSupported()
    {
        // Arrange: the first two sweeps fail, each followed by a purge. Later ones succeed, so the test can't stop the
        // service during a failing sweep.
        (ExpiryService service, Mock<IResourceServiceClient> client) = NewService(
            call => call <= 2 ? throw new NotSupportedException("No expiry after all.") : Task.FromResult(0L),
            TimeSpan.FromMilliseconds(20));
        var secondPurge = new TaskCompletionSource();
        int purges = 0;
        client.Setup(c => c.PurgeDueAsync(It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (++purges == 2) secondPurge.TrySetResult();
            return Task.FromResult(0L);
        });

        // Act
        await service.StartAsync(CancellationToken.None);
        Task reached = await Task.WhenAny(secondPurge.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Same(secondPurge.Task, reached);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExpiryService_ShouldFailToStart_WhenTheProviderDoesNotSupportExpiry()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResourcefull(o => o.UseProvider(_ => repository.Object).UseExpiry());
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService sweep = Assert.Single(provider.GetServices<IHostedService>());

        // Act
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => sweep.StartAsync(CancellationToken.None));

        // Assert
        Assert.Contains(nameof(ResourcefullOptionsBuilder.UseExpiry), thrown.Message);
        Assert.Contains(repository.Object.GetType().Name, thrown.Message);
        repository.Verify(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(),
            It.IsAny<ResourceVersionKey?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A client over a provider that takes every write, but whose <c>SupportsExpiry</c> is false.</summary>
    private static (Mock<ResourceRepository> Repository, ResourceContainerClient Client) WithoutExpiry()
    {
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.SetExpiryAsync(It.IsAny<ResourceAddress>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceAddress("c", "a"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v" });
        return (repository, new ResourceContainerClient(repository.Object, null, new ResourcefullOptions(), "c", null));
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreNothing_WhenItSetsAnExpiryAndTheProviderDoesNotSupportIt()
    {
        // Arrange
        (Mock<ResourceRepository> repository, ResourceContainerClient client) = WithoutExpiry();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => client.UploadAsync("a", BinaryData.FromString("x"),
            new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow }));
        repository.Verify(r => r.AddResourceAsync(It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(),
            It.IsAny<ResourceWriteCondition?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldStoreNothing_WhenItSetsAnExpiryAndTheProviderDoesNotSupportIt()
    {
        // Arrange
        (Mock<ResourceRepository> repository, ResourceContainerClient client) = WithoutExpiry();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => client.CopyResourceAsync("a", "c", "b",
            new ResourceCopyOptions { ExpiresAt = DateTimeOffset.UtcNow }));
        repository.Verify(r => r.CopyResourceAsync(It.IsAny<ResourceAddress>(), It.IsAny<string>(), It.IsAny<NewResourceVersion>(),
            It.IsAny<ResourceWriteCondition?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartUploadAsync_ShouldStageNothing_WhenItSetsAnExpiryAndTheProviderDoesNotSupportIt()
    {
        // Arrange
        (Mock<ResourceRepository> repository, ResourceContainerClient client) = WithoutExpiry();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => client.StartUploadAsync("a",
            new ResourceUploadSessionOptions { ExpiresAt = DateTimeOffset.UtcNow }));
        repository.Verify(r => r.StartStagedVersionAsync(It.IsAny<NewResourceVersion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetExpiryAsync_ShouldNotCallTheProvider_WhenItsSupportsExpiryIsFalse()
    {
        // Arrange
        (Mock<ResourceRepository> repository, ResourceContainerClient client) = WithoutExpiry();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => client.SetExpiryAsync("a", null, DateTimeOffset.UtcNow));
        repository.Verify(r => r.SetExpiryAsync(It.IsAny<ResourceAddress>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldStoreNothing_WhenTheExportHasAnExpiryAndTheProviderDoesNotSupportIt()
    {
        // Arrange: an export with an expiry, from a store that has expiry.
        using ServiceProvider source = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient sourceService = source.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await sourceService.CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        using var export = new MemoryStream();
        await sourceService.ExportContainerAsync("c", export);
        export.Position = 0;
        var repository = new Mock<ResourceRepository>();
        var service = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ImportContainerAsync("restored", export));
        repository.Verify(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportVersionsAsync_ShouldStoreNothing_WhenAVersionHasAnExpiryAndTheProviderDoesNotSupportIt()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("c", It.IsAny<CancellationToken>())).ReturnsAsync(new ContainerInfo { Name = "c" });
        var service = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));
        var version = new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v", ExpiresAt = DateTimeOffset.UtcNow };

        async IAsyncEnumerable<ImportedVersion> Versions()
        {
            await Task.Yield();
            yield return new ImportedVersion(version, _ => Task.FromResult<Stream?>(new MemoryStream()));
        }

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => service.ImportVersionsAsync("c", Versions()));
        repository.Verify(r => r.ImportVersionAsync(It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("InMemory")]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    [InlineData("Sqlite")]
    public void OptionalFeatureFlags_ShouldBeTrue_WhenGivenABuiltInProvider(string provider)
    {
        // A flag left false would turn the contract tests of that feature into skips, without failing anything. The file
        // system, S3 and Azure Blob share their flags: FileSystemContractTests checks them.
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            _ = provider switch
            {
                "InMemory" => options.UseInMemory(),
                "SqlServer" => options.UseSqlServer("Server=unused"),
                "PostgreSql" => options.UsePostgreSql("Host=unused"),
                _ => options.UseSqlite("Data Source=unused")
            };
        });
        using ServiceProvider built = services.BuildServiceProvider();
        using IServiceScope scope = built.CreateScope();

        ResourceRepository repository = scope.ServiceProvider.GetRequiredService<ResourceRepository>();

        Assert.True(repository.SupportsExpiry);
        Assert.True(repository.SupportsRetention);
        Assert.True(repository.SupportsSoftDelete);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteEveryExpiredVersion_WhenThereAreMoreThanABatch()
    {
        // Arrange: more than the 100 versions one batch lists, in a store of their own.
        using ServiceProvider provider = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        for (int i = 0; i < 101; i++)
            await container.UploadAsync($"r{i}", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });

        // Act
        long deleted = await service.DeleteExpiredAsync();

        // Assert
        Assert.Equal(101, deleted);
        Assert.Empty(await container.ListResourcesAsync().ToListAsync());
    }

    /// <summary>Makes the container named <c>frozen</c> read-only once <see cref="Frozen"/> is set.</summary>
    private sealed class FrozenPolicies : IResourcePolicyProvider
    {
        public static bool Frozen;

        public ValueTask<ContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Frozen && containerName == "frozen" ? new ContainerPolicy { ReadOnly = true } : null);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldReachLaterVersions_WhenMoreThanABatchExpiringFirstAreKept()
    {
        // Arrange: a full batch of read-only versions that expire before the one that should go.
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseContainerPolicies(cacheFor: TimeSpan.Zero).UsePolicyProvider<FrozenPolicies>())
            .BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient frozen = await service.CreateContainerAsync("frozen");
        IResourceContainerClient other = await service.CreateContainerAsync("other");
        DateTimeOffset expired = DateTimeOffset.UtcNow.AddDays(-1);
        for (int i = 0; i < 101; i++)
            await frozen.UploadAsync($"r{i}", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = expired });
        await other.UploadAsync("r", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = expired.AddMinutes(1) });
        FrozenPolicies.Frozen = true;
        try
        {
            // Act
            long deleted = await service.DeleteExpiredAsync();

            // Assert
            Assert.Equal(1, deleted);
            Assert.False(await other.ExistsAsync("r"));
            Assert.Equal(101, (await frozen.ListResourcesAsync().ToListAsync()).Count);
        }
        finally
        {
            FrozenPolicies.Frozen = false;
        }
    }

    /// <summary>A cleanup handler whose audit call times out, as an HTTP client's does.</summary>
    private sealed class TimingOutHandler : IResourceCleanupHandler
    {
        public ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken) =>
            cleanup.Version.Name == "first" ? throw new TaskCanceledException("The audit call timed out.") : ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldCarryOn_WhenTheHandlersOwnCallTimesOut()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseCleanupHandler<TimingOutHandler>()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        DateTimeOffset expired = DateTimeOffset.UtcNow.AddDays(-1);
        await container.UploadAsync("first", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = expired });
        await container.UploadAsync("second", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = expired.AddMinutes(1) });

        // Act
        Exception? sweep = await Record.ExceptionAsync(() => service.DeleteExpiredAsync());

        // Assert
        Assert.IsType<TaskCanceledException>(Assert.Single(Assert.IsType<AggregateException>(sweep).InnerExceptions));
        Assert.True(await container.ExistsAsync("first"));
        Assert.False(await container.ExistsAsync("second"));
    }

    /// <summary>A cleanup handler whose audit system is down.</summary>
    private sealed class FailingHandler : IResourceCleanupHandler
    {
        public ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The audit system is down.");
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldCountEveryFailureButKeepOnlyTheFirstTen_WhenTheHandlerFailsForAll()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseCleanupHandler<FailingHandler>()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        for (int i = 0; i < 12; i++)
            await container.UploadAsync($"r{i}", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });

        // Act
        var sweep = await Assert.ThrowsAsync<AggregateException>(() => service.DeleteExpiredAsync());

        // Assert
        Assert.Equal(10, sweep.InnerExceptions.Count);
        Assert.StartsWith("12 expired versions weren't deleted", sweep.Message);
        Assert.Equal(12, (await container.ListResourcesAsync().ToListAsync()).Count);
    }

    [Fact]
    public void UseExpiry_ShouldRegisterOneHostedService_WhenCalledTwice()
    {
        // Arrange
        var services = new ServiceCollection();
        var builder = new ResourcefullOptionsBuilder(services);

        // Act
        builder.UseExpiry().UseExpiry(TimeSpan.FromSeconds(5));

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.IsType<ExpirySettings>(Assert.Single(services, d => d.ServiceType == typeof(ExpirySettings)).ImplementationInstance).Interval);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseExpiry(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbandonedUploadsDeletedAfter_ShouldBeADayOrNull_WhenTheExpirySweepRunsOrNot(bool expiry)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(o =>
        {
            o.UseInMemory();
            if (expiry) o.UseExpiry();
        });

        // Act
        using ServiceProvider provider = services.BuildServiceProvider();
        TimeSpan? after = provider.GetRequiredService<IOptions<ResourcefullOptions>>().Value.AbandonedUploadsDeletedAfter;

        // Assert
        Assert.Equal(expiry ? TimeSpan.FromDays(1) : null, after);
    }

    [Fact]
    public async Task ExpiryMembers_ShouldThrowNotSupported_WhenTheProviderDoesNotImplementThem()
    {
        // Arrange
        ResourceRepository repository = new Mock<ResourceRepository> { CallBase = true }.Object;
        var location = new ResourceAddress("c", "a");

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetExpiredAsync(DateTimeOffset.UtcNow, 10));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.DeleteExpiredVersionAsync(location, "v", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SetExpiryAsync(location, "v", null));
    }

    [Fact]
    public async Task UploadAsync_ShouldDeleteThePrunedVersionAtOnce_WhenExpiryIsOnButTheProviderDoesNotSupportIt()
    {
        // Arrange: a provider that doesn't override SetExpiryAsync, which throws NotSupportedException by default.
        var repository = new Mock<ResourceRepository> { CallBase = true };
        repository.Setup(r => r.AddResourceAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.Is<ResourceWriteCondition?>(c => c == null || !c.CreateOnly), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WrittenContent(1, "sha256:written", DateTimeOffset.UtcNow));
        repository.Setup(r => r.GetResourceVersionsAsync(new ResourceAddress("c", "a"), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v2" },
            new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v1" }
        ]);
        var handler = new Mock<IResourceCleanupHandler>();
        var client = new ResourceContainerClient(repository.Object, null, new ResourcefullOptions(), "c",
            null, cleanupHandler: handler.Object, expiry: new ExpirySettings(TimeSpan.FromMinutes(1)));

        // Act
        await client.UploadAsync("a", BinaryData.FromString("x"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert
        handler.Verify(h => h.OnCleanupAsync(It.Is<ResourceCleanup>(c => c.Reason == ResourceCleanupReason.Pruned && c.Version.VersionId == "v1"),
            It.IsAny<CancellationToken>()));
        repository.Verify(r => r.DeleteResourceAsync(new ResourceAddress("c", "a"), "v1", It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()));
    }

    /// <summary>Refuses the versions of the names a test lists, as an audit system that's down for them.</summary>
    private sealed class RefusingCleanupHandler : IResourceCleanupHandler
    {
        public static readonly ConcurrentDictionary<string, bool> Refusing = new();

        public ValueTask OnCleanupAsync(ResourceCleanup cleanup, CancellationToken cancellationToken) =>
            Refusing.ContainsKey(cleanup.Version.ContainerName + "/" + cleanup.Version.Name)
                || Refusing.ContainsKey(cleanup.Version.ContainerName + "/*")
                ? throw new InvalidOperationException("The audit system is down.")
                : ValueTask.CompletedTask;
    }

    /// <summary>A container whose names each have two versions under a limit of one, as an import leaves them.</summary>
    private static async Task<(IResourceServiceClient Service, string Container)> OverLimitAsync(ServiceProvider services, int names)
    {
        IResourceServiceClient service = services.GetRequiredService<IResourceServiceClient>();
        string name = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await service.CreateContainerAsync(name);
        // Each upload's own prune is refused, so the older versions stay.
        RefusingCleanupHandler.Refusing[name + "/*"] = true;
        for (int i = 0; i < names; i++)
        {
            await container.UploadAsync($"n{i:D5}", BinaryData.FromString("1"));
            await container.UploadAsync($"n{i:D5}", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        }

        RefusingCleanupHandler.Refusing.TryRemove(name + "/*", out _);
        return (service, name);
    }

    private static ServiceProvider NewRefusingServices() => new ServiceCollection()
        .AddResourcefull(o => o.UseInMemory().UseVersioning(1).UseCleanupHandler<RefusingCleanupHandler>())
        .BuildServiceProvider();

    [Fact]
    public async Task PruneAsync_ShouldPruneEveryName_WhenTheContainerHasMoreThanOnePageOfNames()
    {
        // Arrange
        using ServiceProvider services = NewRefusingServices();
        (IResourceServiceClient service, string name) = await OverLimitAsync(services, ResourceListOptions.DefaultPageSize + 1);

        // Act
        long pruned = await service.GetContainerClient(name).PruneAsync();

        // Assert
        Assert.Equal(ResourceListOptions.DefaultPageSize + 1, pruned);
        Assert.Single(await service.GetContainerClient(name).GetResourceVersionsAsync($"n{ResourceListOptions.DefaultPageSize:D5}"));
    }

    [Fact]
    public async Task PruneAsync_ShouldPruneTheOtherNamesThenThrow_WhenOneNamesPruneFails()
    {
        // Arrange: the cleanup handler refuses the first name.
        using ServiceProvider services = NewRefusingServices();
        (IResourceServiceClient service, string name) = await OverLimitAsync(services, 3);
        RefusingCleanupHandler.Refusing[name + "/n00000"] = true;

        // Act
        Task act() => service.GetContainerClient(name).PruneAsync();

        // Assert
        AggregateException failed = await Assert.ThrowsAsync<AggregateException>(act);
        Assert.IsType<InvalidOperationException>(Assert.Single(failed.InnerExceptions));
        IResourceContainerClient container = service.GetContainerClient(name);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("n00000")).Count);
        Assert.Single(await container.GetResourceVersionsAsync("n00001"));
        Assert.Single(await container.GetResourceVersionsAsync("n00002"));
    }
}
