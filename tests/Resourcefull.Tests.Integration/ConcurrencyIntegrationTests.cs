using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Reproduces the production workload that previously produced SQL Server deadlock victims (1205):
/// many jobs, each with its own container, concurrently inserting/listing/deleting against the shared
/// tables. With the redesigned clustered key, RCSI and the retry policy in place, none of these
/// should surface as a failure to the caller.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class ConcurrencyIntegrationTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task ConcurrentContainerWorkflows_ShouldNotSurfaceDeadlocks_WhenManyJobsRunAgainstTheSharedTables()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using var provider = fixture.BuildServiceProvider();

        const int workers = 25;
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);

        IEnumerable<Task<Workflow>> workflows = Enumerable.Range(0, workers).Select(async index =>
        {
            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();

            var containerName = $"job-{Guid.NewGuid():N}";
            await service.CreateResourceContainerAsync(containerName);
            var container = service.GetResourceContainerClient(containerName);

            var artifactName = $"artifact-{Guid.NewGuid():N}";
            await container.UploadAsync(artifactName, new MemoryStream(payload));
            IReadOnlyList<ResourceInfo> listed = await container.GetResourcesAsync();
            ResourceContent? downloaded = await container.DownloadContentAsync(artifactName);
            bool deleted = await service.DeleteResourceContainerIfExistsAsync(containerName);
            return new Workflow(containerName, artifactName, listed, downloaded?.Content.ToArray(), deleted);
        });

        // Act
        Workflow[] results = await Task.WhenAll(workflows);

        // Assert: each job saw exactly its own upload, whole, and removed its container.
        using var checkScope = provider.CreateScope();
        var check = checkScope.ServiceProvider.GetRequiredService<IResourceServiceClient>();
        foreach (Workflow result in results)
        {
            Assert.Equal(payload, result.Downloaded);
            Assert.Equal(result.ArtifactName, Assert.Single(result.Listed).Name);
            Assert.True(result.Deleted);
            Assert.False(await check.ContainerExistsAsync(result.ContainerName));
        }
    }

    private sealed record Workflow(string ContainerName, string ArtifactName, IReadOnlyList<ResourceInfo> Listed, byte[]? Downloaded,
        bool Deleted);
}
