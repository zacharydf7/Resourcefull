using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    // Many callers writing the same resource or container name at once. Each test releases all its writers together
    // so they genuinely race, then checks the state they leave behind.
    private const int Writers = 16;

    [SkippableFact]
    public async Task UploadAsync_ShouldLetExactlyOneSucceed_WhenConcurrentCreateOnlyUploadsTargetOneName()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Exception?[] outcomes = await RaceAsync(provider, _ =>
            container.UploadAsync("report.txt", BinaryData.FromString("x"), new ResourceUploadOptions { CreateOnly = true }));

        // Assert
        Assert.Single(outcomes, o => o is null);
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ResourceConflictException>(o));
        Assert.Single(await container.GetResourceVersionsAsync("report.txt"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldLetExactlyOneSucceed_WhenConcurrentUploadsWithoutOverwriteTargetOneNameWithoutVersioning()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Exception?[] outcomes = await RaceAsync(provider, _ => container.UploadAsync("report.txt", BinaryData.FromString("x")));

        // Assert
        Assert.Single(outcomes, o => o is null);
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ResourceConflictException>(o));
        Assert.Single(await container.GetResourcesAsync());
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldLeaveOneVersion_WhenConcurrentOverwritingUploadsTargetOneNameWithoutVersioning()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Exception?[] outcomes = await RaceAsync(provider, i =>
            container.UploadAsync("report.txt", BinaryData.FromString($"v{i}"), new ResourceUploadOptions { Overwrite = true }));

        // Assert
        Assert.All(outcomes, Assert.Null);
        Assert.True(Assert.Single(await container.GetResourceVersionsAsync("report.txt")).IsLatest);
        Assert.Single(await container.GetResourcesAsync());
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldKeepEveryVersionWithOneCurrent_WhenConcurrentUploadsTargetOneName()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(maxVersions: 100));
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Exception?[] outcomes = await RaceAsync(provider, i => container.UploadAsync("report.txt", BinaryData.FromString($"v{i}")));

        // Assert
        Assert.All(outcomes, Assert.Null);
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("report.txt");
        Assert.Equal(Writers, versions.Count);
        Assert.Single(versions, v => v.IsLatest);
        Assert.Single(await container.GetResourcesAsync());
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldLeaveMaxVersions_WhenConcurrentUploadsTargetOneName()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(maxVersions: 3));
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Exception?[] outcomes = await RaceAsync(provider, i => container.UploadAsync("report.txt", BinaryData.FromString($"v{i}")));

        // Assert
        Assert.All(outcomes, Assert.Null);
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("report.txt");
        Assert.Equal(3, versions.Count);
        Assert.Single(versions, v => v.IsLatest);
    }

    [SkippableFact]
    public async Task CreateResourceContainerAsync_ShouldLetExactlyOneSucceed_WhenCalledConcurrently()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        string name = NewContainerName();

        // Act
        Exception?[] outcomes = await RaceAsync(provider, (_, service) => service.CreateResourceContainerAsync(name));

        // Assert
        Assert.Single(outcomes, o => o is null);
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ResourceConflictException>(o));
    }

    [SkippableFact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldSucceedForEveryCaller_WhenCalledConcurrently()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        string name = NewContainerName();

        // Act
        Exception?[] outcomes = await RaceAsync(provider, (_, service) => service.CreateResourceContainerIfNotExistsAsync(name));

        // Assert
        Assert.All(outcomes, Assert.Null);
        Assert.Single(await ServiceOf(provider).GetResourceContainersAsync(), c => c.Name == name);
    }

    private static Task<Exception?[]> RaceAsync(ServiceProvider provider, Func<int, Task> write) =>
        RaceAsync(provider, (i, _) => write(i));

    /// <summary>Runs <see cref="Writers"/> writes, each in its own scope, released together; null marks a success.</summary>
    private static async Task<Exception?[]> RaceAsync(ServiceProvider provider, Func<int, IResourceServiceClient, Task> write)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Exception?>[] writers = [.. Enumerable.Range(0, Writers).Select(i => Task.Run<Exception?>(async () =>
        {
            using IServiceScope scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
            await start.Task;
            return await Record.ExceptionAsync(() => write(i, service));
        }))];
        start.SetResult();
        return await Task.WhenAll(writers);
    }
}
