using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit;

public class ExpiryTests
{
    /// <summary>The expiry service, over a service client that answers each sweep with <paramref name="sweep"/>.</summary>
    private static (ExpiryService Service, Mock<IResourceServiceClient> Client) NewService(Func<int, Task<int>> sweep, TimeSpan interval)
    {
        var client = new Mock<IResourceServiceClient>();
        int calls = 0;
        client.Setup(c => c.DeleteExpiredAsync(It.IsAny<CancellationToken>())).Returns(() => sweep(++calls));
        ServiceProvider services = new ServiceCollection().AddScoped(_ => client.Object).BuildServiceProvider();
        return (new ExpiryService(services.GetRequiredService<IServiceScopeFactory>(), new ExpirySettings(interval),
            NullLogger<ExpiryService>.Instance), client);
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
            return Task.FromResult(0);
        }, TimeSpan.FromMilliseconds(20));

        // Act
        await service.StartAsync(CancellationToken.None);
        Task reached = await Task.WhenAny(secondSweep.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await service.StopAsync(CancellationToken.None);

        // Assert
        Assert.Same(secondSweep.Task, reached);
        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExpiryService_ShouldStop_WhenTheProviderDoesNotSupportExpiry()
    {
        // Arrange
        (ExpiryService service, Mock<IResourceServiceClient> client) = NewService(
            _ => throw new NotSupportedException("No expiry."), TimeSpan.FromMilliseconds(20));

        // Act
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        client.Verify(c => c.DeleteExpiredAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteEveryExpiredVersion_WhenThereAreMoreThanABatch()
    {
        // Arrange: more than the 100 versions one batch lists, in a store of their own.
        using ServiceProvider provider = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateResourceContainerAsync("c");
        for (int i = 0; i < 101; i++)
            await container.UploadAsync($"r{i}", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });

        // Act
        int deleted = await service.DeleteExpiredAsync();

        // Assert
        Assert.Equal(101, deleted);
        Assert.Empty(await container.GetResourcesAsync());
    }

    /// <summary>Makes the container named <c>frozen</c> read-only once <see cref="Frozen"/> is set.</summary>
    private sealed class FrozenPolicies : IResourcePolicyProvider
    {
        public static bool Frozen;

        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Frozen && containerName == "frozen" ? new ResourceContainerPolicy { ReadOnly = true } : null);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldReachLaterVersions_WhenMoreThanABatchExpiringFirstAreKept()
    {
        // Arrange: a full batch of read-only versions that expire before the one that should go.
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseContainerPolicies(cacheFor: TimeSpan.Zero).UsePolicyProvider<FrozenPolicies>())
            .BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient frozen = await service.CreateResourceContainerAsync("frozen");
        IResourceContainerClient other = await service.CreateResourceContainerAsync("other");
        DateTimeOffset expired = DateTimeOffset.UtcNow.AddDays(-1);
        for (int i = 0; i < 101; i++)
            await frozen.UploadAsync($"r{i}", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = expired });
        await other.UploadAsync("r", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = expired.AddMinutes(1) });
        FrozenPolicies.Frozen = true;
        try
        {
            // Act
            int deleted = await service.DeleteExpiredAsync();

            // Assert
            Assert.Equal(1, deleted);
            Assert.False(await other.ExistsAsync("r"));
            Assert.Equal(101, (await frozen.GetResourcesAsync()).Count);
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
            cleanup.Resource.Name == "first" ? throw new TaskCanceledException("The audit call timed out.") : ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldCarryOn_WhenTheHandlersOwnCallTimesOut()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseCleanupHandler<TimingOutHandler>()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateResourceContainerAsync("c");
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
        IResourceContainerClient container = await service.CreateResourceContainerAsync("c");
        for (int i = 0; i < 12; i++)
            await container.UploadAsync($"r{i}", BinaryData.FromString("x"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) });

        // Act
        var sweep = await Assert.ThrowsAsync<AggregateException>(() => service.DeleteExpiredAsync());

        // Assert
        Assert.Equal(10, sweep.InnerExceptions.Count);
        Assert.StartsWith("12 expired versions weren't deleted", sweep.Message);
        Assert.Equal(12, (await container.GetResourcesAsync()).Count);
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

    [Fact]
    public async Task ExpiryMembers_ShouldThrowNotSupported_WhenAProviderOrClientWrittenFor31IsAsked()
    {
        // Arrange
        IResourceRepository repository = new Mock<IResourceRepository> { CallBase = true }.Object;
        IResourceContainerClient container = new Mock<IResourceContainerClient> { CallBase = true }.Object;
        IResourceServiceClient service = new Mock<IResourceServiceClient> { CallBase = true }.Object;
        var location = new ResourceLocation("c", "a");

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetExpiredAsync(DateTimeOffset.UtcNow, 10));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.DeleteExpiredVersionAsync(location, "v", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<NotSupportedException>(() => repository.SetExpiryAsync(location, "v", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => container.SetExpiryAsync("a", "v", null));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteExpiredAsync());
    }
}
