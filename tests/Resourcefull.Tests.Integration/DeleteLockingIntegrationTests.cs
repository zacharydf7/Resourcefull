using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Integration;

/// <summary>
/// Deleting a large version mustn't lock the whole chunk table: SQL Server escalates a statement's locks to the table
/// once it holds about 5,000 on it, and every other upload then waits until the delete's transaction ends.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class DeleteLockingIntegrationTests(SqlServerFixture fixture)
{
    // More chunks than one statement can lock before SQL Server escalates.
    private const int Chunks = 6_000;

    private async Task<long> LockEscalationsAsync()
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ISNULL(SUM(index_lock_promotion_count), 0) " +
            "FROM sys.dm_db_index_operational_stats(DB_ID(), OBJECT_ID('ResourceChunks'), NULL, NULL)";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Gives a name's current version <see cref="Chunks"/> more chunks of 8,000 bytes, written set-based.</summary>
    private async Task InflateAsync(string containerName, string resourceName)
    {
        await using SqlConnection connection = await fixture.OpenConnectionAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data])
            SELECT r.[Id], n.[i] + 100, CAST(REPLICATE(CAST('x' AS varchar(max)), 8000) AS varbinary(max))
            FROM [Resources] r
            CROSS JOIN (SELECT TOP (@chunks) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS [i]
                        FROM sys.all_objects a CROSS JOIN sys.all_objects b) n
            WHERE r.[ContainerName] = @container AND r.[Name] = @name AND r.[IsLatest] = 1
            """;
        command.Parameters.AddWithValue("@chunks", Chunks);
        command.Parameters.AddWithValue("@container", containerName);
        command.Parameters.AddWithValue("@name", resourceName);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    [SkippableTheory]
    [InlineData("version")]
    [InlineData("name")]
    [InlineData("container")]
    public async Task Delete_ShouldNotLockTheWholeChunkTable_WhenTheVersionHasThousandsOfChunks(string what)
    {
        fixture.SkipIfUnavailable();

        // Arrange: an older version too, so deleting the name deletes more than one.
        using ServiceProvider provider = fixture.BuildServiceProvider(options => options.UseVersioning());
        var service = provider.GetRequiredService<IResourceServiceClient>();
        string containerName = $"c-{Guid.NewGuid():N}";
        IResourceContainerClient container = await service.CreateResourceContainerAsync(containerName);
        await container.UploadAsync("big", BinaryData.FromString("older"));
        ResourceInfo current = await container.UploadAsync("big", BinaryData.FromString("x"));
        await InflateAsync(containerName, "big");
        long before = await LockEscalationsAsync();

        // Act
        bool deleted = what switch
        {
            "version" => await container.DeleteResourceIfExistsAsync("big", current.VersionId),
            "name" => await container.DeleteResourceIfExistsAsync("big"),
            _ => await service.DeleteResourceContainerIfExistsAsync(containerName)
        };

        // Assert
        Assert.True(deleted);
        Assert.Equal(before, await LockEscalationsAsync());
        Assert.Equal(what == "version" ? 1 : 0, (await container.GetResourceVersionsAsync("big")).Count);
    }
}
