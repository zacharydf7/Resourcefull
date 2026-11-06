using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// The SQL Server provider on what early releases stored: versions with no recorded length or hash, for its
/// usage query, its largest-first listing and copies.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class ContainerUsageIntegrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task GetUsageAsync_ShouldAddUpTheChunks_WhenAVersionHasNoRecordedLength()
    {
        fixture.SkipIfUnavailable();

        // Arrange: two versions across several chunks, one of them with its recorded length removed.
        using ServiceProvider provider = fixture.BuildServiceProvider(o => o.UseVersioning());
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync(containerName);
        ResourceInfo legacy = await container.UploadAsync("a", new MemoryStream(new byte[150_000]));
        await container.UploadAsync("a", new MemoryStream(new byte[10]), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await using (SqlConnection connection = await fixture.OpenConnectionAsync())
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE [Resources] SET [ContentLength] = NULL WHERE [VersionId] = @v";
            command.Parameters.AddWithValue("@v", legacy.VersionId);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        ContainerUsage usage = await container.GetUsageAsync();

        // Assert
        Assert.Equal(new ContainerUsage(150_010, 1, 2), usage);
    }

    [Fact]
    public async Task GetTopVersionsAsync_ShouldListAVersionWithoutARecordedLengthLast_WhenGivenLargestFirst()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a large version with its recorded length removed, and a small one.
        using ServiceProvider provider = fixture.BuildServiceProvider(o => o.UseVersioning());
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync(containerName);
        ResourceInfo legacy = await container.UploadAsync("a", new MemoryStream(new byte[150_000]));
        ResourceInfo small = await container.UploadAsync("a", new MemoryStream(new byte[10]), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await using (SqlConnection connection = await fixture.OpenConnectionAsync())
        await using (SqlCommand command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE [Resources] SET [ContentLength] = NULL WHERE [VersionId] = @v";
            command.Parameters.AddWithValue("@v", legacy.VersionId);
            await command.ExecuteNonQueryAsync();
        }

        // Act
        IReadOnlyList<ResourceInfo> top = await container.GetTopVersionsAsync(ResourceVersionOrder.LargestFirst);

        // Assert
        Assert.Equal([small.VersionId, legacy.VersionId], top.Select(v => v.VersionId));
    }

    [Fact]
    public async Task CopyAndMove_ShouldCheckTheStoredSize_WhenAVersionHasNoRecordedLength()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a 150,000-byte version with no recorded length, and a destination that allows 100,000 bytes a resource.
        using ServiceProvider provider = fixture.BuildServiceProvider(o => o.UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<SmallDestinations>());
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        string sourceName = $"c-{Guid.NewGuid():N}", destinationName = $"small-{Guid.NewGuid():N}";
        IResourceContainerClient source = await service.CreateContainerAsync(sourceName);
        await service.CreateContainerAsync(destinationName);
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

    /// <summary>Gives containers whose names start with <c>small-</c> a limit of 100,000 bytes a resource.</summary>
    private sealed class SmallDestinations : IResourcePolicyProvider
    {
        public ValueTask<ContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(containerName.StartsWith("small-", StringComparison.Ordinal)
                ? new ContainerPolicy { MaxResourceSize = 100_000 }
                : null);
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldReadAndHashTheContent_WhenTheSourceHasNoRecordedHash()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        using ServiceProvider provider = fixture.BuildServiceProvider();
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync(containerName);
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
        Assert.Equal(new ResourceOrigin(containerName, "a", legacy.VersionId), (await container.GetResourceInfoAsync("b"))!.CopiedFrom);
        Assert.Equal("hello", (await container.DownloadContentAsync("b"))!.Content.ToString());
    }
}
