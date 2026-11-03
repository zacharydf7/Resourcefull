using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Resourcefull.EntityFramework;
using Resourcefull.Providers;
using Resourcefull.SqlServer.Models;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Integration")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Performance")]

namespace Resourcefull.SqlServer;

/// <summary>
/// The SQL Server provider's repository: the shared EF Core one, with SQL Server's own statements, locks and errors.
/// </summary>
internal class ResourceSqlRepository(IDbContextFactory<ResourceDbContext> dbContextFactory, TimeProvider? timeProvider = null,
    string? storeName = null) : EntityFrameworkRepository(dbContextFactory, timeProvider, storeName)
{
    private const int SqlForeignKeyViolation = 547;
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;

    /// <summary>
    /// How many chunks one statement deletes. SQL Server locks the whole chunk table, holding up every upload, once a
    /// statement holds 5,000 locks on it. That's usually one lock per chunk, but SQL Server 2019 can also lock a chunk's
    /// content pages, about five for a full one: deleting 999 full chunks took 6,240 locks there once the table held
    /// other versions' chunks. 500 stays well under, at about 3,100. A version with more chunks than this has them
    /// deleted this many at a time before it goes; a smaller one takes them with it.
    /// </summary>
    internal const int ChunksPerDeleteStatement = 500;

    /// <summary>
    /// The error a name's lock raises when SQL Server chooses it as a deadlock victim, or it times out: transient, like a
    /// deadlock (1205), so the retry policy <c>UseSqlServer</c> sets retries the write.
    /// </summary>
    internal const int NameLockNotGrantedError = 50001;

    protected override int ChunksDeletedPerStatement => ChunksPerDeleteStatement;

    /// <summary>Releases before 3.0 stored versions without a length, and before that without a hash.</summary>
    protected override bool MayHoldUnmeasuredVersions => true;

    protected override bool IsForeignKeyViolation(Exception exception) =>
        (exception as SqlException ?? exception.InnerException as SqlException) is { Number: SqlForeignKeyViolation };

    protected override bool IsUniqueViolation(Exception exception) =>
        (exception as SqlException ?? exception.InnerException as SqlException) is
        {
            Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation
        };

    protected override void CountAbandonedUploads(int deleted) => SqlTelemetry.CountAbandonedUploads(deleted, StoreName);

    protected override Task<List<int>> LockStagedVersionAsync(ResourceDbContext attempt, long resourceId, DateTimeOffset? staleBefore,
        CancellationToken cancellationToken) =>
        attempt.Database.SqlQuery<int>(attempt.QualifyTables($"""
            SELECT (SELECT COUNT(*) FROM [ResourceChunks] c WHERE c.[ResourceId] = r.[Id]) AS [Value]
            FROM [Resources] r WITH (UPDLOCK, ROWLOCK)
            WHERE r.[Id] = {resourceId} AND r.[StagedAt] IS NOT NULL
                AND ({staleBefore} IS NULL OR r.[StagedAt] < {staleBefore})
            """)).ToListAsync(cancellationToken);

    protected override Task<int> MoveSessionLengthAsync(ResourceDbContext attempt, long resourceId, long expected, long after,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        attempt.Database.ExecuteSqlAsync(attempt.QualifyTables($"""
            UPDATE [Resources] SET [ContentLength] = {after}, [StagedAt] = {now}
            WHERE [Id] = {resourceId} AND [StagedAt] IS NOT NULL AND [ContentLength] = {expected}
            """), cancellationToken);

    protected override async Task<long?> SessionLengthAsync(ResourceDbContext attempt, long resourceId,
        CancellationToken cancellationToken) =>
        (await attempt.Database.SqlQuery<long?>(attempt.QualifyTables($"""
            SELECT [ContentLength] AS [Value] FROM [Resources] WHERE [Id] = {resourceId} AND [StagedAt] IS NOT NULL
            """)).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    protected override async Task<SessionRow?> LockSessionAsync(ResourceDbContext attempt, ResourceLocation resource, string versionId,
        CancellationToken cancellationToken) =>
        (await attempt.Database.SqlQuery<SessionRow>(attempt.QualifyTables($"""
            SELECT [Id], [ContentLength], [StagedAt] FROM [Resources] WITH (UPDLOCK, ROWLOCK)
            WHERE [ContainerName] = {resource.ContainerName} AND [Name] = {resource.ResourceName} AND [VersionId] = {versionId}
                AND [StagedAt] IS NOT NULL AND [ContentLength] IS NOT NULL
            """)).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    protected override string InsertChunks(DbCommand command, List<ResourceChunkModel> batch, DateTimeOffset? stampedAt)
    {
        var sql = new StringBuilder("INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data]) VALUES ");
        command.Parameters.Add(new SqlParameter("@resourceId", batch[0].ResourceId));
        for (int i = 0; i < batch.Count; i++)
        {
            sql.Append(i == 0 ? "" : ", ").Append($"(@resourceId, @index{i}, @data{i})");
            command.Parameters.Add(new SqlParameter($"@index{i}", batch[i].Index));
            command.Parameters.Add(new SqlParameter($"@data{i}", SqlDbType.VarBinary, -1) { Value = batch[i].Data });
        }
        if (stampedAt is { } now)
        {
            sql.Append("; UPDATE [Resources] SET [StagedAt] = @now WHERE [Id] = @resourceId");
            command.Parameters.Add(new SqlParameter("@now", now));
        }
        return sql.ToString();
    }

    protected override Task CopyChunksAsync(ResourceDbContext attempt, long fromId, long toId, CancellationToken cancellationToken) =>
        attempt.Database.ExecuteSqlAsync(attempt.QualifyTables(
            $"INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data]) SELECT {toId}, [Index], [Data] FROM [ResourceChunks] WHERE [ResourceId] = {fromId}"),
            cancellationToken);

    protected override async Task<ChunkSpan> ChunkSpanAsync(ResourceDbContext dbContext, long resourceId, long start, long end,
        CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<ChunkSpan>(dbContext.QualifyTables($"""
            SELECT MAX(CASE WHEN [End] <= {start} THEN [Index] END) AS [AfterIndex],
                MAX(CASE WHEN [End] <= {start} THEN [End] END) AS [Start],
                MIN(CASE WHEN [End] >= {end} THEN [Index] END) AS [ThroughIndex]
            FROM (SELECT [Index], SUM(CAST(DATALENGTH([Data]) AS bigint)) OVER (ORDER BY [Index] ROWS UNBOUNDED PRECEDING) AS [End]
                FROM [ResourceChunks] WHERE [ResourceId] = {resourceId}) AS [Chunks]
            """)).ToListAsync(cancellationToken).ConfigureAwait(false))[0];

    protected override string ReadChunks(DbCommand command, long resourceId, int? lastIndex, int? throughIndex)
    {
        command.Parameters.Add(new SqlParameter("@resourceId", resourceId));
        command.Parameters.Add(new SqlParameter("@lastIndex", SqlDbType.Int) { Value = (object?)lastIndex ?? DBNull.Value });
        if (throughIndex is not null)
            command.Parameters.Add(new SqlParameter("@throughIndex", SqlDbType.Int) { Value = throughIndex });
        return $"SELECT TOP ({ChunksPerReadPage}) [Index], DATALENGTH([Data]), [Data] FROM [ResourceChunks] " +
            "WHERE [ResourceId] = @resourceId AND (@lastIndex IS NULL OR [Index] > @lastIndex)" +
            (throughIndex is null ? "" : " AND [Index] <= @throughIndex") + " ORDER BY [Index]";
    }

    protected override Task<int> DeleteSomeChunksAsync(ResourceDbContext dbContext, long resourceId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(dbContext.QualifyTables(
            $"DELETE TOP ({ChunksPerDeleteStatement}) FROM [ResourceChunks] WHERE [ResourceId] = {resourceId}"), cancellationToken);

    protected override async Task<ResourceContainerUsage> MeasureContainerAsync(ResourceDbContext dbContext, string resourceContainerName,
        CancellationToken cancellationToken)
    {
        // One aggregate over the container's version rows, from their recorded lengths, which doesn't touch the chunks.
        // Only if an early release stored versions without a length are their chunks' lengths added up, separately.
        UsageRow usage = (await dbContext.Database.SqlQuery<UsageRow>(dbContext.QualifyTables($"""
            SELECT COALESCE(SUM(r.[ContentLength]), 0) AS [StoredSize],
                COUNT_BIG(CASE WHEN r.[IsLatest] = 1 THEN 1 END) AS [ResourceCount],
                COUNT_BIG(*) AS [VersionCount],
                COUNT_BIG(CASE WHEN r.[ContentLength] IS NULL THEN 1 END) AS [Unmeasured]
            FROM [Resources] r
            WHERE r.[ContainerName] = {resourceContainerName} AND r.[StagedAt] IS NULL
            """)).ToListAsync(cancellationToken).ConfigureAwait(false))[0];
        long storedSize = usage.StoredSize;
        if (usage.Unmeasured > 0)
        {
            storedSize += await dbContext.Database.SqlQuery<long>(dbContext.QualifyTables($"""
                SELECT COALESCE(SUM(CAST(DATALENGTH(ch.[Data]) AS bigint)), 0) AS [Value]
                FROM [Resources] r JOIN [ResourceChunks] ch ON ch.[ResourceId] = r.[Id]
                WHERE r.[ContainerName] = {resourceContainerName} AND r.[ContentLength] IS NULL AND r.[StagedAt] IS NULL
                """)).SingleAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ResourceContainerUsage(storedSize, usage.ResourceCount, usage.VersionCount);
    }

    protected override async Task<bool> LockVersionInContainerAsync(ResourceDbContext attempt, long resourceId,
        string resourceContainerName, CancellationToken cancellationToken) =>
        (await attempt.Database.SqlQuery<int>(attempt.QualifyTables($"""
            SELECT 1 AS [Value] FROM [Resources] WITH (UPDLOCK, ROWLOCK)
            WHERE [Id] = {resourceId} AND [ContainerName] = {resourceContainerName}
            """)).ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <summary>
    /// A statement per version, in one round trip, rather than loading and tracking each to delete it: SQL Server removes
    /// each version's chunks through the cascade with a seek. One statement for the whole batch, or one that also matches
    /// the container, cascades with a scan of the chunk table instead, which locks all of it. So the versions still in the
    /// container are locked and listed first, by ID, and only those are deleted.
    /// </summary>
    protected override Task DeleteVersionsInContainerAsync(ResourceDbContext attempt, string resourceContainerName, List<long> ids,
        CancellationToken cancellationToken)
    {
        var idParameters = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
        var sql = new StringBuilder("DECLARE @here TABLE ([Id] bigint PRIMARY KEY); " +
            "INSERT INTO @here SELECT [Id] FROM [Resources] WITH (UPDLOCK, ROWLOCK, INDEX([PK_Resources])) " +
            $"WHERE [Id] IN ({idParameters}) AND [ContainerName] = @container;");
        var parameters = new object[ids.Count + 1];
        parameters[0] = new SqlParameter("@container", resourceContainerName);
        for (int i = 0; i < ids.Count; i++)
        {
            sql.Append($"IF EXISTS (SELECT 1 FROM @here WHERE [Id] = @id{i}) ")
                .Append($"DELETE FROM [Resources] WHERE [Id] = @id{i};");
            parameters[i + 1] = new SqlParameter($"@id{i}", ids[i]);
        }
        return attempt.Database.ExecuteSqlRawAsync(ResourceDbContext.QualifyTables(sql.ToString(), attempt.Schema), parameters,
            cancellationToken);
    }

    /// <summary>Takes an exclusive application lock on each name, rather than key-range locks on its rows.</summary>
    protected override async Task LockNamesAsync(ResourceDbContext dbContext, IReadOnlyList<ResourceLocation> names,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0) return;
        var sql = new StringBuilder("DECLARE @result int;");
        var parameters = new object[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            // -3 is a deadlock victim and -1 a timeout, either of which a retry can get past.
            sql.Append($"EXEC @result = sp_getapplock @Resource = @lock{i}, @LockMode = 'Exclusive', ")
                .Append("@LockOwner = 'Transaction';")
                .Append($"IF @result IN (-1, -3) THROW {NameLockNotGrantedError}, ")
                .Append("'The resource name''s lock was chosen as a deadlock victim or timed out.', 1;")
                .Append("IF @result < 0 THROW 50000, 'Could not lock the resource name for writing.', 1;");
            parameters[i] = new SqlParameter($"@lock{i}", NameLockResource(names[i]));
        }
        await dbContext.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The application lock resource for a name. Lock resources are limited to 255 characters, so it's hashed. Stores in
    /// different schemas of the same database share it: a store without a schema uses the database user's default
    /// schema, which can be the one another store was given, so a single lock across every store is what keeps writers
    /// of a name taking turns.
    /// </summary>
    internal static string NameLockResource(ResourceLocation name) =>
        "Resourcefull:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{name.ContainerName}\n{name.ResourceName}")));

    /// <summary>What <see cref="MeasureContainerAsync"/> reads, named as its query's columns.</summary>
    internal sealed class UsageRow
    {
        public long StoredSize { get; set; }
        public long ResourceCount { get; set; }
        public long VersionCount { get; set; }
        public long Unmeasured { get; set; }
    }
}
