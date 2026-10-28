using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The SQL provider learns that a container is missing from the foreign key on the write itself, which only a real
/// SQL Server enforces, so these live here rather than in the unit suite.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class MissingContainerIntegrationTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task UploadAsync_ShouldThrowContainerNotFound_WhenContainerWasNeverCreated()
    {
        fixture.SkipIfUnavailable();

        // Arrange & Act
        using var provider = fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var container = service.GetResourceContainerClient($"missing-{Guid.NewGuid():N}");

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(
            () => container.UploadAsync("doc", new MemoryStream(Encoding.UTF8.GetBytes("v1"))));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowContainerNotFound_WhenContainerIsDeletedAfterTheClientIsObtained()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using var provider = fixture.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        var containerName = $"c-{Guid.NewGuid():N}";
        var container = await service.CreateResourceContainerAsync(containerName);
        await service.DeleteResourceContainerIfExistsAsync(containerName);

        // Act
        var exception = await Record.ExceptionAsync(
            () => container.UploadAsync("doc", new MemoryStream(Encoding.UTF8.GetBytes("v1"))));

        // Assert
        var notFound = Assert.IsType<ResourceContainerNotFoundException>(exception);
        Assert.IsType<DbUpdateException>(notFound.InnerException);
        Assert.Null(await container.GetResourceInfoAsync("doc"));
    }
}
