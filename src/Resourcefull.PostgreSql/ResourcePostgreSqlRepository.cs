using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Resourcefull.EntityFramework;
using Resourcefull.Providers;
using Resourcefull.PostgreSql.Models;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("Resourcefull.Tests.PostgreSql")]

namespace Resourcefull.PostgreSql;

/// <summary>
/// The PostgreSQL provider's repository: the shared EF Core one, with PostgreSQL's own statements, locks and errors.
/// </summary>
internal class ResourcePostgreSqlRepository(IDbContextFactory<ResourceDbContext> dbContextFactory, TimeProvider? timeProvider = null,
    string? storeName = null, IReadOnlySet<string>? searchableMetadataKeys = null)
    : EntityFrameworkRepository(dbContextFactory, timeProvider, storeName, searchableMetadataKeys)
{
    private const string ForeignKeyViolation = "23503";
    private const string UniqueViolation = "23505";

    /// <summary>
    /// How many chunks one statement deletes, so deleting a large version takes many short statements rather than one
    /// that could outlast the command timeout. The cascade deleted a 1 GB version's chunks in about 2 seconds on a local
    /// server, so for one of 15 GB, or a smaller one on slower storage, it would outlast the default 30-second timeout,
    /// and again on every retry. A version with more chunks than this has them deleted this many at a time before it
    /// goes; a smaller one takes them with it, through the cascade.
    /// </summary>
    internal const int ChunksPerDeleteStatement = 1_000;

    protected override int ChunksDeletedPerStatement => ChunksPerDeleteStatement;

    /// <summary>PostgreSQL's text can't hold U+0000.</summary>
    protected override string? DatabaseRefusingNul => "PostgreSQL";

    /// <summary>
    /// The time, to the microsecond PostgreSQL keeps it to, so a version's times read back as the write returned them.
    /// </summary>
    protected override DateTimeOffset Now()
    {
        DateTimeOffset now = base.Now();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    protected override bool IsForeignKeyViolation(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is { SqlState: ForeignKeyViolation };

    protected override bool IsUniqueViolation(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is { SqlState: UniqueViolation };

    protected override void CountAbandonedUploads(long deleted) => PostgreSqlTelemetry.CountAbandonedUploads(deleted, StoreName);

    protected override Task<List<int>> LockStagedVersionAsync(ResourceDbContext attempt, long resourceId, DateTimeOffset? staleBefore,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? before = staleBefore?.ToUniversalTime();
        return attempt.Database.SqlQuery<int>(attempt.QualifyTables($"""
            SELECT (SELECT COUNT(*)::integer FROM "ResourceChunks" c WHERE c."ResourceId" = r."Id") AS "Value"
            FROM "Resources" r
            WHERE r."Id" = {resourceId} AND r."StagedAt" IS NOT NULL
                AND ({before}::timestamptz IS NULL OR r."StagedAt" < {before})
            FOR UPDATE OF r
            """)).ToListAsync(cancellationToken);
    }

    protected override Task<int> MoveSessionLengthAsync(ResourceDbContext attempt, long resourceId, long expected, long after,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        attempt.Database.ExecuteSqlAsync(attempt.QualifyTables($"""
            UPDATE "Resources" SET "ContentLength" = {after}, "StagedAt" = {now}
            WHERE "Id" = {resourceId} AND "StagedAt" IS NOT NULL AND "ContentLength" = {expected}
            """), cancellationToken);

    protected override async Task<long?> SessionLengthAsync(ResourceDbContext attempt, long resourceId,
        CancellationToken cancellationToken) =>
        (await attempt.Database.SqlQuery<long?>(attempt.QualifyTables($"""
            SELECT "ContentLength" AS "Value" FROM "Resources" WHERE "Id" = {resourceId} AND "StagedAt" IS NOT NULL
            """)).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    protected override async Task<SessionRow?> LockSessionAsync(ResourceDbContext attempt, ResourceAddress resource, string versionId,
        CancellationToken cancellationToken) =>
        (await attempt.Database.SqlQuery<SessionRow>(attempt.QualifyTables($"""
            SELECT "Id", "ContentLength", "StagedAt" FROM "Resources"
            WHERE "ContainerName" = {resource.ContainerName} AND "Name" = {resource.ResourceName} AND "VersionId" = {versionId}
                AND "StagedAt" IS NOT NULL AND "ContentLength" IS NOT NULL
            FOR UPDATE
            """)).ToListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    protected override string InsertChunks(DbCommand command, List<ResourceChunkModel> batch, DateTimeOffset? stampedAt)
    {
        var sql = new StringBuilder("""INSERT INTO "ResourceChunks" ("ResourceId", "Index", "Data") VALUES """);
        command.Parameters.Add(new NpgsqlParameter("resourceId", batch[0].ResourceId));
        for (int i = 0; i < batch.Count; i++)
        {
            sql.Append(i == 0 ? "" : ", ").Append($"(@resourceId, @index{i}, @data{i})");
            command.Parameters.Add(new NpgsqlParameter($"index{i}", batch[i].Index));
            command.Parameters.Add(new NpgsqlParameter($"data{i}", NpgsqlDbType.Bytea) { Value = batch[i].Data });
        }
        if (stampedAt is { } now)
        {
            sql.Append("""; UPDATE "Resources" SET "StagedAt" = @now WHERE "Id" = @resourceId""");
            command.Parameters.Add(new NpgsqlParameter("now", now.ToUniversalTime()));
        }
        return sql.ToString();
    }

    protected override Task CopyChunksAsync(ResourceDbContext attempt, long fromId, long toId, CancellationToken cancellationToken) =>
        attempt.Database.ExecuteSqlAsync(attempt.QualifyTables($"""
            INSERT INTO "ResourceChunks" ("ResourceId", "Index", "Data")
            SELECT {toId}, "Index", "Data" FROM "ResourceChunks" WHERE "ResourceId" = {fromId}
            """), cancellationToken);

    protected override async Task<ChunkSpan> ChunkSpanAsync(ResourceDbContext dbContext, long resourceId, long start, long end,
        CancellationToken cancellationToken) =>
        (await dbContext.Database.SqlQuery<ChunkSpan>(dbContext.QualifyTables($"""
            SELECT MAX(CASE WHEN "End" <= {start} THEN "Index" END) AS "AfterIndex",
                MAX(CASE WHEN "End" <= {start} THEN "End" END) AS "Start",
                MIN(CASE WHEN "End" >= {end} THEN "Index" END) AS "ThroughIndex"
            FROM (SELECT "Index", SUM(octet_length("Data")) OVER (ORDER BY "Index" ROWS UNBOUNDED PRECEDING) AS "End"
                FROM "ResourceChunks" WHERE "ResourceId" = {resourceId}) AS "Chunks"
            """)).ToListAsync(cancellationToken).ConfigureAwait(false))[0];

    protected override string ReadChunks(DbCommand command, long resourceId, int? lastIndex, int? throughIndex)
    {
        command.Parameters.Add(new NpgsqlParameter("resourceId", resourceId));
        command.Parameters.Add(new NpgsqlParameter("lastIndex", NpgsqlDbType.Integer) { Value = (object?)lastIndex ?? DBNull.Value });
        if (throughIndex is not null)
            command.Parameters.Add(new NpgsqlParameter("throughIndex", NpgsqlDbType.Integer) { Value = throughIndex });
        return """SELECT "Index", octet_length("Data")::bigint, "Data" FROM "ResourceChunks" """ +
            """WHERE "ResourceId" = @resourceId AND (@lastIndex IS NULL OR "Index" > @lastIndex)""" +
            (throughIndex is null ? "" : """ AND "Index" <= @throughIndex""") +
            $""" ORDER BY "Index" LIMIT {ChunksPerReadPage}""";
    }

    protected override Task<int> DeleteSomeChunksAsync(ResourceDbContext dbContext, long resourceId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(dbContext.QualifyTables($"""
            DELETE FROM "ResourceChunks" WHERE "ResourceId" = {resourceId} AND "Index" IN (
                SELECT "Index" FROM "ResourceChunks" WHERE "ResourceId" = {resourceId} ORDER BY "Index" LIMIT {ChunksPerDeleteStatement})
            """), cancellationToken);

    protected override async Task<ContainerUsage> MeasureContainerAsync(ResourceDbContext dbContext, string containerName,
        CancellationToken cancellationToken)
    {
        // One aggregate over the container's version rows, from their recorded lengths, which doesn't touch the chunks;
        // the bin's rows are added up apart.
        UsageRow usage = (await dbContext.Database.SqlQuery<UsageRow>(dbContext.QualifyTables($"""
            SELECT COALESCE(SUM(r."ContentLength") FILTER (WHERE r."DeletedAt" IS NULL), 0)::bigint AS "StoredSize",
                COUNT(*) FILTER (WHERE r."IsLatest") AS "ResourceCount",
                COUNT(*) FILTER (WHERE r."DeletedAt" IS NULL) AS "VersionCount",
                COALESCE(SUM(r."ContentLength") FILTER (WHERE r."DeletedAt" IS NOT NULL), 0)::bigint AS "DeletedSize",
                COUNT(r."DeletedAt") AS "DeletedVersionCount"
            FROM "Resources" r
            WHERE r."ContainerName" = {containerName} AND r."StagedAt" IS NULL
            """)).ToListAsync(cancellationToken).ConfigureAwait(false))[0];
        return new ContainerUsage
        {
            StoredSize = usage.StoredSize,
            ResourceCount = usage.ResourceCount,
            VersionCount = usage.VersionCount,
            DeletedSize = usage.DeletedSize,
            DeletedVersionCount = usage.DeletedVersionCount
        };
    }

    protected override async Task<bool> LockVersionInContainerAsync(ResourceDbContext attempt, long resourceId,
        string containerName, DateTimeOffset now, CancellationToken cancellationToken) =>
        (await attempt.Database.SqlQuery<int>(attempt.QualifyTables($"""
            SELECT 1 AS "Value" FROM "Resources"
            WHERE "Id" = {resourceId} AND "ContainerName" = {containerName}
              AND ("StagedAt" IS NOT NULL OR (NOT "LegalHold" AND ("RetainUntil" IS NULL OR "RetainUntil" <= {now.ToUniversalTime()})))
            FOR UPDATE
            """)).ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

    /// <summary>
    /// Locks the container's row: a write adding a version locks it to check the foreign key, and holds that lock until
    /// it commits, so this waits for one under way and keeps any more from starting.
    /// </summary>
    protected override async Task<bool> LockEmptyContainerAsync(ResourceDbContext attempt, string containerName,
        CancellationToken cancellationToken)
    {
        await attempt.Database.SqlQuery<int>(attempt.QualifyTables($"""
            SELECT 1 AS "Value" FROM "ResourceContainers" WHERE "Name" = {containerName} FOR UPDATE
            """)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return !await attempt.Resources.IgnoreQueryFilters().AnyAsync(r => r.ContainerName == containerName, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>One statement for the whole batch; the cascade deletes the versions' chunks.</summary>
    protected override Task DeleteVersionsInContainerAsync(ResourceDbContext attempt, string containerName, List<long> ids,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        long[] batch = [.. ids];
        return attempt.Database.ExecuteSqlAsync(attempt.QualifyTables($"""
            DELETE FROM "Resources" WHERE "Id" = ANY({batch}) AND "ContainerName" = {containerName}
              AND ("StagedAt" IS NOT NULL OR (NOT "LegalHold" AND ("RetainUntil" IS NULL OR "RetainUntil" <= {now.ToUniversalTime()})))
            """), cancellationToken);
    }

    /// <summary>Takes an exclusive advisory lock on each name, rather than locks on its rows.</summary>
    protected override async Task LockNamesAsync(ResourceDbContext dbContext, IReadOnlyList<ResourceAddress> names,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0) return;
        var sql = new StringBuilder();
        var parameters = new object[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            // A deadlock (40P01) is transient, so the retry policy UsePostgreSql sets retries the write.
            sql.Append($"SELECT pg_advisory_xact_lock(@lock{i});");
            parameters[i] = new NpgsqlParameter($"lock{i}", NameLockKey(names[i]));
        }
        await dbContext.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The advisory lock key for a name: advisory locks take a 64-bit key, so the name is hashed into one. Stores in
    /// different schemas of the same database share it: a store without a schema uses the connection's search path,
    /// which can find the schema another store was given, so a single lock across every store is what keeps writers of
    /// a name taking turns.
    /// </summary>
    internal static long NameLockKey(ResourceAddress name) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{name.ContainerName}\n{name.ResourceName}")));

    /// <summary>What <see cref="MeasureContainerAsync"/> reads, named as its query's columns.</summary>
    internal sealed class UsageRow
    {
        public long StoredSize { get; set; }
        public long ResourceCount { get; set; }
        public long VersionCount { get; set; }
        public long DeletedSize { get; set; }
        public long DeletedVersionCount { get; set; }
    }
}
