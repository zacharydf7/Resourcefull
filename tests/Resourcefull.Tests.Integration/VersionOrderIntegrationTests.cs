using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;
using Resourcefull.Tests.Contract;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The store, not the writer's clock, decides which version is current and how versions are ordered. Writes go through
/// the repository with a clock set before each one, so each version is stamped as an application server whose clock is
/// off would stamp it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class VersionOrderIntegrationTests(SqlServerFixture fixture)
{
    private readonly SqlServerFixture _fixture = fixture;

    [SkippableFact]
    public async Task ResourceSqlRepository_ShouldOrderAndPromoteByStoreOrder_WhenWritersClocksDisagree()
    {
        _fixture.SkipIfUnavailable();

        // Arrange: "first" is written by a server whose clock is an hour ahead of the servers writing the later two.
        using var provider = _fixture.BuildServiceProvider();
        var service = provider.GetRequiredService<IResourceServiceClient>();
        var containerName = $"c-{Guid.NewGuid():N}";
        await service.CreateResourceContainerAsync(containerName);
        var clock = new ManualTimeProvider();
        var repository = new ResourceSqlRepository(provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>(), clock);
        clock.Now = DateTime.UtcNow.AddHours(1);
        await repository.AddResourceAsync(containerName, Version(containerName, "first"), new MemoryStream());
        clock.Now = DateTime.UtcNow;
        await repository.AddResourceAsync(containerName, Version(containerName, "second"), new MemoryStream());
        await repository.AddResourceAsync(containerName, Version(containerName, "third"), new MemoryStream());

        // Act
        List<ResourceInfo> versions = await repository.GetResourceVersionsAsync(containerName, "n");
        await repository.DeleteResourceAsync(containerName, "n", "third");
        ResourceInfo? promoted = await repository.GetResourceInfoAsync(containerName, "n");

        // Assert
        Assert.Equal(["third", "second", "first"], versions.Select(v => v.VersionId));
        Assert.Equal("second", promoted!.VersionId);
        Assert.True(promoted.IsLatest);
    }

    private static ResourceInfo Version(string containerName, string versionId) =>
        new() { ContainerName = containerName, Name = "n", VersionId = versionId };
}
