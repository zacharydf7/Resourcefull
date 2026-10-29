using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit;

public class ResourcefullServiceCollectionExtensionsTests
{
    [Fact]
    public void AddResourcefull_ShouldThrowAnException_WhenNoProviderIsConfigured()
    {
        // Arrange & Act
        var services = new ServiceCollection();

        // Assert
        Assert.Throws<InvalidOperationException>(() => services.AddResourcefull(options => { }));
    }

    [Fact]
    public void AddResourcefull_ShouldAddResourceServiceClient_WhenProviderIsConfigured()
    {
        // Arrange
        var services = new ServiceCollection();

        // Mock
        var resourcefullOptionsBuilderAction = new Mock<Action<ResourcefullOptionsBuilder>>();
        resourcefullOptionsBuilderAction.Setup(action => action.Invoke(It.IsAny<ResourcefullOptionsBuilder>()))
            .Callback((ResourcefullOptionsBuilder options) => options.UseInMemory());

        // Act
        services.AddResourcefull(resourcefullOptionsBuilderAction.Object);

        // Assert
        Assert.NotNull(services.SingleOrDefault(x => x.ServiceType == typeof(IResourceServiceClient)));
    }

    [Fact]
    public async Task AddResourcefull_ShouldLogThroughTheHostsLogging_WhenPruningFailsAfterAnUpload()
    {
        // Arrange: a store that keeps two versions of the name and can't delete the older one.
        var failure = new InvalidOperationException("storage unavailable");
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.AddResourceAsync("reports", It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new WrittenContent(0, "sha256:empty", DateTimeOffset.UnixEpoch));
        repository.Setup(r => r.GetResourceVersionsAsync("reports", "name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = "reports", Name = "name", VersionId = "new" },
                new() { ContainerName = "reports", Name = "name", VersionId = "old" }
            ]);
        repository.Setup(r => r.DeleteResourceAsync("reports", "name", "old", It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var loggerProvider = new Mock<ILoggerProvider>();
        loggerProvider.Setup(p => p.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        loggerProvider.Setup(p => p.CreateLogger("Resourcefull.ResourceContainerClient")).Returns(logger.Object);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loggerProvider.Object));
        services.AddResourcefull(options => options.UseInMemory());
        services.AddScoped(_ => repository.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IResourceServiceClient>().GetResourceContainerClient("reports");

        // Act
        await client.UploadAsync("name", new MemoryStream(), new ResourceUploadOptions { Overwrite = true });

        // Assert
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), failure,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}