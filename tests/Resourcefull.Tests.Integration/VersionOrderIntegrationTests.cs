using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The store, not the writer's clock, decides which version is current and how versions are ordered. Writes go through
/// the repository so each version can carry a skewed <see cref="ResourceInfo.LastModified"/>, as it would from an
/// application server whose clock is off.
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
        var repository = new ResourceSqlRepository(provider.GetRequiredService<IDbContextFactory<ResourceDbContext>>());
        await repository.AddResourceAsync(containerName, Version(containerName, "first", DateTime.UtcNow.AddHours(1)), new MemoryStream());
        await repository.AddResourceAsync(containerName, Version(containerName, "second", DateTime.UtcNow), new MemoryStream());
        await repository.AddResourceAsync(containerName, Version(containerName, "third", DateTime.UtcNow), new MemoryStream());

        // Act
        List<ResourceInfo> versions = await repository.GetResourceVersionsAsync(containerName, "n");
        await repository.DeleteResourceAsync(containerName, "n", "third");
        ResourceInfo? promoted = await repository.GetResourceInfoAsync(containerName, "n");

        // Assert
        Assert.Equal(["third", "second", "first"], versions.Select(v => v.VersionId));
        Assert.Equal("second", promoted!.VersionId);
        Assert.True(promoted.IsLatest);
    }

    private static ResourceInfo Version(string containerName, string versionId, DateTime lastModified) =>
        new() { ContainerName = containerName, Name = "n", VersionId = versionId, LastModified = lastModified };
}
