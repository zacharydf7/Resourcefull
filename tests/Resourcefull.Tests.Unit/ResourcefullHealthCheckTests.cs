using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Resourcefull.InMemory;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

public class ResourcefullHealthCheckTests
{
    private static async Task<HealthReportEntry> CheckAsync(Action<IServiceCollection>? replace = null, HealthStatus? failureStatus = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddResourcefull(options => options.UseInMemory());
        services.AddHealthChecks().AddResourcefullCheck(failureStatus: failureStatus);
        replace?.Invoke(services);
        await using ServiceProvider provider = services.BuildServiceProvider();
        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        return report.Entries["resourcefull"];
    }

    [Fact]
    public async Task AddResourcefullCheck_ShouldReportHealthy_WhenTheStorageCanBeRead()
    {
        // Act
        HealthReportEntry entry = await CheckAsync();

        // Assert
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Equal("inmemory", entry.Data["provider"]);
    }

    [Theory]
    [InlineData(null, HealthStatus.Unhealthy)]
    [InlineData(HealthStatus.Degraded, HealthStatus.Degraded)]
    public async Task AddResourcefullCheck_ShouldReportTheFailureStatusWithTheException_WhenTheStorageFails(HealthStatus? failureStatus,
        HealthStatus expected)
    {
        // Arrange
        var failure = new InvalidOperationException("storage is down");
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        // Act
        HealthReportEntry entry = await CheckAsync(services => services.Replace(ServiceDescriptor.Scoped(_ => repository.Object)), failureStatus);

        // Assert
        Assert.Equal(expected, entry.Status);
        Assert.Same(failure, entry.Exception);
    }

    [Fact]
    public async Task AddResourcefullCheck_ShouldOnlyRead_WhenCalled()
    {
        // Arrange: a strict repository refuses anything but the one read.
        var repository = new Mock<ResourceRepository>(MockBehavior.Strict);
        repository.Setup(r => r.GetContainerInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContainerInfo?)null);

        // Act
        HealthReportEntry entry = await CheckAsync(services => services.Replace(ServiceDescriptor.Scoped(_ => repository.Object)));

        // Assert
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        repository.Verify(r => r.GetContainerInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
