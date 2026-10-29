using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Integration;

/// <summary>The SQL Server provider's usage query on versions stored by early releases, which have no recorded length.</summary>
[Collection(SqlServerCollection.Name)]
public class ContainerUsageIntegrationTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task GetUsageAsync_ShouldAddUpTheChunks_WhenAVersionHasNoRecordedLength()
    {
        fixture.SkipIfUnavailable();

        // Arrange: two versions across several chunks, one of them with its recorded length removed.
        using ServiceProvider provider = fixture.BuildServiceProvider(o => o.UseVersioning());
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync(containerName);
        ResourceInfo legacy = await container.UploadAsync("a", new MemoryStream(new byte[150_000]));
        await container.UploadAsync("a", new MemoryStream(new byte[10]));
        await using (SqlConnection connection = await fixture.OpenConnectionAsync())
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE [Resources] SET [ContentLength] = NULL WHERE [VersionId] = @v";
            command.Parameters.AddWithValue("@v", legacy.VersionId);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        ResourceContainerUsage usage = await container.GetUsageAsync();

        // Assert
        Assert.Equal(new ResourceContainerUsage(150_010, 1, 2), usage);
    }
}
