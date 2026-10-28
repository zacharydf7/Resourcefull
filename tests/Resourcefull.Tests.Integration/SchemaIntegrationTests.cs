namespace Resourcefull.Tests.Integration;

/// <summary>
/// Verifies the migration chain produces the intended schema
/// </summary>
[Collection(SqlServerCollection.Name)]
public class SchemaIntegrationTests(SqlServerFixture fixture)
{
    private readonly SqlServerFixture _fixture = fixture;

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await _fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [SkippableFact]
    public async Task ResourcesTable_ShouldBeClusteredOnContainerNameNameVersion_WhenMigrationsAreApplied()
    {
        _fixture.SkipIfUnavailable();

        // Act
        var clusteredIndexName = (string?)await ScalarAsync(
            "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID('Resources') AND type_desc = 'CLUSTERED'");

        // Assert
        Assert.Equal("UX_Resources_Container_Name_Version", clusteredIndexName);
    }

    [SkippableFact]
    public async Task ResourcesTable_ShouldHaveFilteredLatestIndex_WhenMigrationsAreApplied()
    {
        _fixture.SkipIfUnavailable();

        // Act
        var hasFilter = await ScalarAsync(
            "SELECT has_filter FROM sys.indexes WHERE name = 'IX_Resources_Latest' AND object_id = OBJECT_ID('Resources')");

        // Assert
        Assert.Equal(true, hasFilter);
    }

    [SkippableFact]
    public async Task ResourceChunksTable_ShouldBeClusteredOnResourceIdThenIndex_WhenMigrationsAreApplied()
    {
        _fixture.SkipIfUnavailable();

        // Act
        var keyColumns = (string?)await ScalarAsync(@"
            SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('ResourceChunks') AND i.type_desc = 'CLUSTERED'");

        // Assert
        Assert.Equal("ResourceId,Index", keyColumns);
    }

    [SkippableFact]
    public async Task Database_ShouldHaveReadCommittedSnapshotEnabled_WhenMigrationsAreApplied()
    {
        _fixture.SkipIfUnavailable();

        // Act
        var isRcsiOn = await ScalarAsync("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()");

        // Assert
        Assert.Equal(true, isRcsiOn);
    }
}
