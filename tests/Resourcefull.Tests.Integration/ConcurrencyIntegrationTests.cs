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

        IEnumerable<Task> workflows = Enumerable.Range(0, workers).Select(async index =>
        {
            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IResourceServiceClient>();

            var containerName = $"job-{Guid.NewGuid():N}";
            await service.CreateResourceContainerAsync(containerName);
            var container = service.GetResourceContainerClient(containerName);

            await container.UploadAsync($"artifact-{Guid.NewGuid():N}", new MemoryStream(payload));
            _ = await container.GetResourcesAsync();
            await service.DeleteResourceContainerIfExistsAsync(containerName);
        });

        // Act
        var exception = await Record.ExceptionAsync(() => Task.WhenAll(workflows));

        // Assert
        Assert.Null(exception);
    }
}
