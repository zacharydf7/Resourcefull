using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Resourcefull.Tests.Integration;

[Collection(SqlServerCollection.Name)]
public class NameIntegrationTests(SqlServerFixture fixture)
{
    private readonly SqlServerFixture _fixture = fixture;

    [SkippableFact]
    public async Task CreateResourceContainerAsync_ShouldCreateBoth_WhenNamesDifferOnlyInCase()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var suffix = Guid.NewGuid().ToString("N");

        // Act
        await service.CreateResourceContainerAsync($"Docs-{suffix}");
        await service.CreateResourceContainerAsync($"docs-{suffix}");

        // Assert
        Assert.True(await service.ContainerExistsAsync($"Docs-{suffix}"));
        Assert.True(await service.ContainerExistsAsync($"docs-{suffix}"));
        Assert.False(await service.ContainerExistsAsync($"DOCS-{suffix}"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldKeepBothResources_WhenNamesDifferOnlyInCase()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var container = await service.CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");

        // Act
        await container.UploadAsync("report.txt", BinaryData.FromString("lower"));
        await container.UploadAsync("Report.txt", BinaryData.FromString("upper"));

        // Assert
        Assert.Equal(2, (await container.GetResourcesAsync()).Count);
        Assert.Equal("lower", (await container.DownloadContentAsync("report.txt"))!.Content.ToString());
        Assert.Equal("upper", (await container.DownloadContentAsync("Report.txt"))!.Content.ToString());
        Assert.Null(await container.GetResourceInfoAsync("REPORT.TXT"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldRoundTripTheResource_WhenNamesAreAtTheMaximumLengths()
    {
        _fixture.SkipIfUnavailable();

        // Arrange
        using var provider = _fixture.BuildServiceProvider(o => o.UseVersioning());
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        // The documented limits: 128 characters for a container name, 256 for a resource name.
        var containerName = Guid.NewGuid().ToString("N").PadRight(128, 'c');
        var resourceName = new string('r', 256);
        var payload = Encoding.UTF8.GetBytes("longest names");
        var container = await service.CreateResourceContainerAsync(containerName);

        // Act
        await container.UploadAsync(resourceName, new MemoryStream(payload));
        var content = await container.DownloadContentAsync(resourceName);

        // Assert
        Assert.Equal(payload, content!.Content.ToArray());
    }
}
