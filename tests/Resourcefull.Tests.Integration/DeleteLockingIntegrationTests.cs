using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Utilities;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Deleting a large version mustn't lock the whole chunk table: SQL Server escalates a statement's locks to the table
/// once it holds about 5,000 on it, and every other upload then waits until the delete's transaction ends.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class DeleteLockingIntegrationTests(SqlServerFixture fixture)
{
    private async Task<long> LockEscalationsAsync()
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ISNULL(SUM(index_lock_promotion_count), 0) " +
            "FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('ResourceChunks'), NULL, NULL)";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Gives a name's current version <paramref name="chunks"/> more chunks of <paramref name="bytes"/> each, written set-based.</summary>
    private async Task InflateAsync(string containerName, string resourceName, int chunks, int bytes)
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data])
            SELECT r.[Id], n.[i] + 100, CAST(REPLICATE(CAST('x' AS varchar(max)), @bytes) AS varbinary(max))
            FROM [Resources] r
            CROSS JOIN (SELECT TOP (@chunks) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [i]
                        FROM sys.all_objects a CROSS JOIN sys.all_objects b) n
            WHERE r.[ContainerName] = @container AND r.[Name] = @name AND r.[IsLatest] = 1
            """;
        command.Parameters.AddWithValue("@chunks", chunks);
        command.Parameters.AddWithValue("@bytes", bytes);
        command.Parameters.AddWithValue("@container", containerName);
        command.Parameters.AddWithValue("@name", resourceName);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    // More chunks than one statement can delete before SQL Server escalates, deleted a statement at a time; and, with
    // the one the upload stored, as many full-size chunks as go with their version in one statement, whose content
    // pages are stored apart from their rows.
    public static TheoryData<string, int, int> Cases => new()
    {
        { "version", 6_000, 8_000 },
        { "name", 6_000, 8_000 },
        { "container", 6_000, 8_000 },
        { "expire", 6_000, 8_000 },
        { "version", ResourceSqlRepository.ChunksPerDeleteStatement - 1, ResourceChunker.ChunkSize },
        { "name", ResourceSqlRepository.ChunksPerDeleteStatement - 1, ResourceChunker.ChunkSize },
        { "container", ResourceSqlRepository.ChunksPerDeleteStatement - 1, ResourceChunker.ChunkSize },
        { "expire", ResourceSqlRepository.ChunksPerDeleteStatement - 1, ResourceChunker.ChunkSize },
    };

    [SkippableTheory]
    [MemberData(nameof(Cases))]
    public async Task Delete_ShouldNotLockTheWholeChunkTable_WhenTheVersionHasThousandsOfChunks(string what, int chunks, int bytes)
    {
        fixture.SkipIfUnavailable();

        // Arrange: an older version too, so deleting the name deletes more than one.
        using ServiceProvider provider = fixture.BuildServiceProvider(options => options.UseVersioning());
        var service = provider.GetRequiredService<IResourceServiceClient>();
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await service.CreateResourceContainerAsync(containerName);
        await container.UploadAsync("big", BinaryData.FromString("older"));
        ResourceInfo current = await container.UploadAsync("big", BinaryData.FromString("x"),
            what == "expire" ? new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1) } : null);
        await InflateAsync(containerName, "big", chunks, bytes);
        long before = await LockEscalationsAsync();

        // Act
        bool deleted = what switch
        {
            "version" => await container.DeleteResourceIfExistsAsync("big", current.VersionId),
            "name" => await container.DeleteResourceIfExistsAsync("big"),
            "expire" => (await provider.GetRequiredService<IResourceRepository>().DeleteExpiredVersionsAsync(
                [(new ResourceLocation(containerName, "big"), current.VersionId)], DateTimeOffset.UtcNow)).Count == 1,
            _ => await service.DeleteResourceContainerIfExistsAsync(containerName)
        };

        // Assert
        Assert.True(deleted);
        Assert.Equal(before, await LockEscalationsAsync());
        Assert.Equal(what is "version" or "expire" ? 1 : 0, (await container.GetResourceVersionsAsync("big")).Count);
    }
}
