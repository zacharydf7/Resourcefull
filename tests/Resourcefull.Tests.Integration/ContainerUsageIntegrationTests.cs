using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The SQL Server provider on what early releases stored or read: versions with no recorded length or hash, for its
/// usage query and for copies, and the stored URI that 2.x reads, for moves.
/// </summary>
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

    [SkippableFact]
    public async Task CopyAndMove_ShouldCheckTheStoredSize_WhenAVersionHasNoRecordedLength()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a 150,000-byte version with no recorded length, and a destination that allows 100,000 bytes a resource.
        using ServiceProvider provider = fixture.BuildServiceProvider(o => o.UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<SmallDestinations>());
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        string sourceName = $"c-{Guid.NewGuid():N}", destinationName = $"small-{Guid.NewGuid():N}";
        IResourceContainerClient source = await service.CreateResourceContainerAsync(sourceName);
        await service.CreateResourceContainerAsync(destinationName);
        ResourceInfo legacy = await source.UploadAsync("a", new MemoryStream(new byte[150_000]));
        await using (SqlConnection connection = await fixture.OpenConnectionAsync())
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE [Resources] SET [ContentLength] = NULL WHERE [VersionId] = @v";
            command.Parameters.AddWithValue("@v", legacy.VersionId);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        Exception? copy = await Record.ExceptionAsync(() => source.CopyResourceAsync("a", destinationName, "a"));
        Exception? move = await Record.ExceptionAsync(() => source.MoveResourceAsync("a", destinationName, "a"));

        // Assert
        Assert.All(new[] { copy, move }, e => Assert.Equal(ResourcePolicyRule.MaxResourceSize, Assert.IsType<ResourcePolicyViolationException>(e).Rule));
        Assert.True(await source.ExistsAsync("a"));
    }

    [SkippableFact]
    public async Task MoveResourceAsync_ShouldClearTheStoredUri_WhenMoved()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a URI provider, so the version is stored with a URI, which 2.x reads, naming its old place.
        using ServiceProvider provider = fixture.BuildServiceProvider(o => o.SetUriProvider<NamedUris>());
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync(containerName);
        ResourceInfo stored = await container.UploadAsync("a", BinaryData.FromString("x"));

        // Act
        ResourceInfo? moved = await container.MoveResourceAsync("a", containerName, "b");

        // Assert
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT [Uri] FROM [Resources] WHERE [VersionId] = @v";
        command.Parameters.AddWithValue("@v", stored.VersionId);
        Assert.Equal(DBNull.Value, await command.ExecuteScalarAsync());
        Assert.Equal("https://files/b", moved!.Uri);
    }

    private sealed class NamedUris : IUriProvider
    {
        public string? GetAbsoluteUri(ResourcefullContext context) => $"https://files/{context.ResourceName}";
    }

    /// <summary>Gives containers whose names start with <c>small-</c> a limit of 100,000 bytes a resource.</summary>
    private sealed class SmallDestinations : IResourcePolicyProvider
    {
        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(containerName.StartsWith("small-", StringComparison.Ordinal)
                ? new ResourceContainerPolicy { MaxResourceSize = 100_000 }
                : null);
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldReadAndHashTheContent_WhenTheSourceHasNoRecordedHash()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using ServiceProvider provider = fixture.BuildServiceProvider();
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync(containerName);
        ResourceInfo legacy = await container.UploadAsync("a", BinaryData.FromString("hello"));
        await using (SqlConnection connection = await fixture.OpenConnectionAsync())
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE [Resources] SET [ContentHash] = NULL WHERE [VersionId] = @v";
            command.Parameters.AddWithValue("@v", legacy.VersionId);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        ResourceInfo? copy = await container.CopyResourceAsync("a", containerName, "b");

        // Assert
        Assert.Equal(legacy.ContentHash, copy!.ContentHash);
        Assert.Equal(legacy.ContentHash, (await container.GetResourceInfoAsync("b"))!.ContentHash);
        Assert.Equal(new ResourceOrigin(containerName, "a", legacy.VersionId!), (await container.GetResourceInfoAsync("b"))!.CopiedFrom);
        Assert.Equal("hello", (await container.DownloadContentAsync("b"))!.Content.ToString());
    }
}
