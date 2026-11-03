using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Resourcefull.EntityFramework;
using Resourcefull.Providers;
using Resourcefull.Sqlite.Models;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Sqlite")]

namespace Resourcefull.Sqlite;

/// <summary>
/// The SQLite provider's repository: the shared EF Core one, with SQLite's own statements and errors. SQLite has one
/// writer at a time: a transaction takes the database's write lock as it begins, so writers take turns whatever names
/// they write, and no name needs a lock of its own.
/// </summary>
internal class ResourceSqliteRepository(IDbContextFactory<ResourceDbContext> dbContextFactory, TimeProvider? timeProvider = null,
    string? storeName = null) : EntityFrameworkRepository(dbContextFactory, timeProvider, storeName)
{
    private const int ForeignKeyViolation = 787;
    private const int UniqueViolation = 2067;
    private const int PrimaryKeyViolation = 1555;

    /// <summary>
    /// How many chunks one statement deletes, which bounds each statement's work. It doesn't shorten the write lock,
    /// though: the delete's transaction holds it until every chunk is gone.
    /// </summary>
    internal const int ChunksPerDeleteStatement = 1_000;

    protected override int ChunksDeletedPerStatement => ChunksPerDeleteStatement;

    /// <summary>SQLite's text functions, such as the ones a prefix is matched with, stop at U+0000.</summary>
    protected override string? DatabaseRefusingNul => "SQLite";

    protected override bool IsForeignKeyViolation(Exception exception) =>
        (exception as SqliteException ?? exception.InnerException as SqliteException) is { SqliteExtendedErrorCode: ForeignKeyViolation };

    protected override bool IsUniqueViolation(Exception exception) =>
        (exception as SqliteException ?? exception.InnerException as SqliteException) is
        {
            SqliteExtendedErrorCode: UniqueViolation or PrimaryKeyViolation
        };

    protected override void CountAbandonedUploads(int deleted) => SqliteTelemetry.CountAbandonedUploads(deleted, StoreName);

    /// <summary>
    /// Adds an exact match with <c>instr</c>: EF Core matches a prefix with <c>LIKE</c>, which in SQLite ignores the case
    /// of ASCII letters. Neither can seek the index, so it also bounds the names by where those with the prefix start and
    /// end in SQLite's order, and the seek reads only them. The start is the prefix, unless the listing continues after
    /// a name that starts with it, which is the tighter start: SQLite seeks by one lower bound only. The end is
    /// <see cref="AfterPrefix"/>.
    /// </summary>
    protected override IQueryable<ResourceModel> WithNamePrefix(IQueryable<ResourceModel> query, string prefix, string? afterName)
    {
        query = base.WithNamePrefix(query, prefix, afterName).Where(r => r.Name.IndexOf(prefix) == 0);
        if (afterName?.StartsWith(prefix, StringComparison.Ordinal) != true) query = query.Where(r => string.Compare(r.Name, prefix) >= 0);
        return AfterPrefix(prefix) is { } end ? query.Where(r => string.Compare(r.Name, end) < 0) : query;
    }

    /// <inheritdoc cref="WithNamePrefix"/>
    protected override IQueryable<ResourceContainerModel> WithContainerPrefix(IQueryable<ResourceContainerModel> query, string prefix,
        string? afterName)
    {
        query = base.WithContainerPrefix(query, prefix, afterName).Where(c => c.Name.IndexOf(prefix) == 0);
        if (afterName?.StartsWith(prefix, StringComparison.Ordinal) != true) query = query.Where(c => string.Compare(c.Name, prefix) >= 0);
        return AfterPrefix(prefix) is { } end ? query.Where(c => string.Compare(c.Name, end) < 0) : query;
    }

    /// <summary>
    /// The first string, in Unicode code point order (SQLite's <c>BINARY</c> order), after every string that starts with
    /// <paramref name="prefix"/>: the prefix, less any U+10FFFF at its end, with its last character one higher.
    /// <see langword="null"/> when there's none, as for a prefix of only U+10FFFF, or when the prefix has a surrogate
    /// without its pair.
    /// </summary>
    internal static string? AfterPrefix(string prefix)
    {
        List<int> codePoints = [];
        for (int i = 0; i < prefix.Length; i++)
        {
            if (char.IsSurrogatePair(prefix, i)) codePoints.Add(char.ConvertToUtf32(prefix[i], prefix[++i]));
            else if (char.IsSurrogate(prefix[i])) return null;
            else codePoints.Add(prefix[i]);
        }
        while (codePoints is [.., 0x10FFFF]) codePoints.RemoveAt(codePoints.Count - 1);
        if (codePoints.Count == 0) return null;
        // Surrogates aren't characters, so the next character after U+D7FF is U+E000.
        codePoints[^1] = codePoints[^1] == 0xD7FF ? 0xE000 : codePoints[^1] + 1;
        return string.Concat(codePoints.Select(char.ConvertFromUtf32));
    }

    // Apart from InsertChunks, whose raw SQL converts its time to ticks itself, the statements that read or write a time
    // are queries EF Core writes, so their times go through the model's conversion to ticks.

    protected override Task<List<int>> LockStagedVersionAsync(ResourceDbContext attempt, long resourceId, DateTimeOffset? staleBefore,
        CancellationToken cancellationToken) =>
        attempt.Resources.IgnoreQueryFilters()
            .Where(r => r.Id == resourceId && r.StagedAt != null && (staleBefore == null || r.StagedAt < staleBefore))
            .Select(r => r.Chunks!.Count)
            .ToListAsync(cancellationToken);

    protected override Task<int> MoveSessionLengthAsync(ResourceDbContext attempt, long resourceId, long expected, long after,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        attempt.Resources.IgnoreQueryFilters()
            .Where(r => r.Id == resourceId && r.StagedAt != null && r.ContentLength == expected)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ContentLength, after).SetProperty(r => r.StagedAt, now), cancellationToken);

    protected override async Task<long?> SessionLengthAsync(ResourceDbContext attempt, long resourceId,
        CancellationToken cancellationToken) =>
        await attempt.Resources.IgnoreQueryFilters()
            .Where(r => r.Id == resourceId && r.StagedAt != null)
            .Select(r => r.ContentLength)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    protected override Task<SessionRow?> LockSessionAsync(ResourceDbContext attempt, ResourceLocation resource, string versionId,
        CancellationToken cancellationToken) =>
        attempt.Resources.IgnoreQueryFilters()
            .Where(r => r.ContainerName == resource.ContainerName && r.Name == resource.ResourceName && r.VersionId == versionId
                && r.StagedAt != null && r.ContentLength != null)
            .Select(r => new SessionRow { Id = r.Id, ContentLength = r.ContentLength!.Value, StagedAt = r.StagedAt!.Value })
            .FirstOrDefaultAsync(cancellationToken);

    protected override string InsertChunks(DbCommand command, List<ResourceChunkModel> batch, DateTimeOffset? stampedAt)
    {
        var sql = new StringBuilder("""INSERT INTO "ResourceChunks" ("ResourceId", "Index", "Data") VALUES """);
        command.Parameters.Add(new SqliteParameter("@resourceId", batch[0].ResourceId));
        for (int i = 0; i < batch.Count; i++)
        {
            sql.Append(i == 0 ? "" : ", ").Append($"(@resourceId, @index{i}, @data{i})");
            command.Parameters.Add(new SqliteParameter($"@index{i}", batch[i].Index));
            command.Parameters.Add(new SqliteParameter($"@data{i}", SqliteType.Blob) { Value = batch[i].Data });
        }
        if (stampedAt is { } now)
        {
            // As its UTC ticks, which is how the model keeps a time.
            sql.Append("""; UPDATE "Resources" SET "StagedAt" = @now WHERE "Id" = @resourceId""");
            command.Parameters.Add(new SqliteParameter("@now", now.UtcTicks));
        }
        return sql.ToString();
    }

    protected override Task CopyChunksAsync(ResourceDbContext attempt, long fromId, long toId, CancellationToken cancellationToken) =>
        attempt.Database.ExecuteSqlAsync($"""
            INSERT INTO "ResourceChunks" ("ResourceId", "Index", "Data")
            SELECT {toId}, "Index", "Data" FROM "ResourceChunks" WHERE "ResourceId" = {fromId}
            """, cancellationToken);

    protected override async Task<ChunkSpan> ChunkSpanAsync(ResourceDbContext dbContext, long resourceId, long start, long end,
        CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<ChunkSpan>($"""
            SELECT MAX(CASE WHEN "End" <= {start} THEN "Index" END) AS "AfterIndex",
                MAX(CASE WHEN "End" <= {start} THEN "End" END) AS "Start",
                MIN(CASE WHEN "End" >= {end} THEN "Index" END) AS "ThroughIndex"
            FROM (SELECT "Index", SUM(length("Data")) OVER (ORDER BY "Index" ROWS UNBOUNDED PRECEDING) AS "End"
                FROM "ResourceChunks" WHERE "ResourceId" = {resourceId}) AS "Chunks"
            """).ToListAsync(cancellationToken).ConfigureAwait(false))[0];

    protected override string ReadChunks(DbCommand command, long resourceId, int? lastIndex, int? throughIndex)
    {
        command.Parameters.Add(new SqliteParameter("@resourceId", resourceId));
        command.Parameters.Add(new SqliteParameter("@lastIndex", SqliteType.Integer) { Value = (object?)lastIndex ?? DBNull.Value });
        if (throughIndex is not null)
            command.Parameters.Add(new SqliteParameter("@throughIndex", SqliteType.Integer) { Value = throughIndex });
        return """SELECT "Index", length("Data"), "Data" FROM "ResourceChunks" """ +
            """WHERE "ResourceId" = @resourceId AND (@lastIndex IS NULL OR "Index" > @lastIndex)""" +
            (throughIndex is null ? "" : """ AND "Index" <= @throughIndex""") +
            $""" ORDER BY "Index" LIMIT {ChunksPerReadPage}""";
    }

    /// <summary>By their row IDs, since SQLite takes <c>LIMIT</c> on <c>DELETE</c> only when compiled with an option for it.</summary>
    protected override Task<int> DeleteSomeChunksAsync(ResourceDbContext dbContext, long resourceId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync($"""
            DELETE FROM "ResourceChunks" WHERE rowid IN (
                SELECT rowid FROM "ResourceChunks" WHERE "ResourceId" = {resourceId} ORDER BY "Index" LIMIT {ChunksPerDeleteStatement})
            """, cancellationToken);

    protected override async Task<ResourceContainerUsage> MeasureContainerAsync(ResourceDbContext dbContext, string resourceContainerName,
        CancellationToken cancellationToken)
    {
        // One aggregate over the container's version rows, from their recorded lengths, which doesn't touch the chunks.
        UsageRow usage = (await dbContext.Database.SqlQuery<UsageRow>($"""
            SELECT COALESCE(SUM(r."ContentLength"), 0) AS "StoredSize",
                COUNT(CASE WHEN r."IsLatest" THEN 1 END) AS "ResourceCount",
                COUNT(*) AS "VersionCount"
            FROM "Resources" r
            WHERE r."ContainerName" = {resourceContainerName} AND r."StagedAt" IS NULL
            """).ToListAsync(cancellationToken).ConfigureAwait(false))[0];
        return new ResourceContainerUsage(usage.StoredSize, usage.ResourceCount, usage.VersionCount);
    }

    protected override Task<bool> LockVersionInContainerAsync(ResourceDbContext attempt, long resourceId,
        string resourceContainerName, CancellationToken cancellationToken) =>
        attempt.Resources.IgnoreQueryFilters()
            .AnyAsync(r => r.Id == resourceId && r.ContainerName == resourceContainerName, cancellationToken);

    /// <summary>One statement for the whole batch; the cascade deletes the versions' chunks.</summary>
    protected override Task DeleteVersionsInContainerAsync(ResourceDbContext attempt, string resourceContainerName, List<long> ids,
        CancellationToken cancellationToken) =>
        attempt.Resources.IgnoreQueryFilters()
            .Where(r => ids.Contains(r.Id) && r.ContainerName == resourceContainerName)
            .ExecuteDeleteAsync(cancellationToken);

    /// <summary>Nothing to do: the transaction holds the database's write lock from its start.</summary>
    protected override Task LockNamesAsync(ResourceDbContext dbContext, IReadOnlyList<ResourceLocation> names,
        CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>What <see cref="MeasureContainerAsync"/> reads, named as its query's columns.</summary>
    internal sealed class UsageRow
    {
        public long StoredSize { get; set; }
        public long ResourceCount { get; set; }
        public long VersionCount { get; set; }
    }
}
