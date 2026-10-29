using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.Utilities;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Integration")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Performance")]

namespace Resourcefull.SqlServer;

internal class ResourceSqlRepository(IDbContextFactory<ResourceDbContext> dbContextFactory, TimeProvider? timeProvider = null) : IResourceRepository
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private const int SqlForeignKeyViolation = 547;
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;
    private const int MaxResourcesPerDeleteBatch = 100;

    // About 1 MB of content per round trip while uploading and downloading.
    private const int ChunksPerUploadBatch = 16;
    private const int ChunksPerReadPage = 16;

    /// <summary>
    /// Caps the content one batch of a container delete removes, counted in chunks, so no single transaction has to log
    /// a whole container. A resource bigger than the cap still goes in a batch of its own.
    /// </summary>
    internal int ChunksPerDeleteBatch { get; init; } = 256;

    public Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default) =>
        AddResourceAsync(resourceContainerName, resourceInfo, stream, new ResourceWriteCondition { CreateOnly = createOnly }, cancellationToken);

    public async Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream,
        ResourceWriteCondition condition, CancellationToken cancellationToken = default)
    {
        bool createOnly = condition.CreateOnly;
        ResourceConditions? conditions = condition.Conditions;

        // The content streams into the version's chunks a batch at a time, so memory stays bounded whatever its size,
        // all in one transaction, so none of it is visible until the version is complete and current. The name's lock
        // is taken only at the end, to make the version current, so writers of one name queue for that switch rather
        // than for each other's content.
        WrittenContent? written = null;
        bool contentRead = false;
        Exception? lastFailure = null;
        // Where the content starts, so a retry can read it again from there.
        long? start = stream.CanSeek ? stream.Position : null;

        async Task AttemptAsync(CancellationToken ct)
        {
            using ResourceDbContext attempt = NewWriteContext();

            if (await attempt.Resources.AnyAsync(r => r.ContainerName == resourceContainerName
                && r.Name == resourceInfo.Name && r.VersionId == resourceInfo.VersionId, ct))
            {
                // A retry after a commit whose acknowledgement was lost finds its own version already stored.
                if (written != null) return;
                throw new ResourceConflictException($"Resource {resourceInfo.Name} already has a version {resourceInfo.VersionId}.");
            }

            // Any other retry has to read the content again, once some of it has been read.
            if (contentRead && start == null)
                throw new InvalidOperationException($"Storing resource {resourceInfo.Name} failed with a transient error, and its content can't be read again to retry.", lastFailure);
            if (contentRead) stream.Position = start!.Value;

            await using var transaction = await attempt.Database.BeginTransactionAsync(ct);

            // Checked again under the lock below; this only spares reading the content of an upload that will fail.
            if (createOnly && await attempt.Resources.AnyAsync(r => r.ContainerName == resourceContainerName
                && r.Name == resourceInfo.Name && r.IsLatest, ct))
                throw new ResourceConflictException($"Resource with name {resourceInfo.Name} already exists.");

            // Inserted first, so a missing container is reported before any content is read.
            var resource = new ResourceModel
            {
                ContainerName = resourceContainerName,
                Name = resourceInfo.Name,
                VersionId = resourceInfo.VersionId,
                LastModified = _clock.GetUtcNow(),
                IsLatest = false,
                ContentType = resourceInfo.ContentType,
                Uri = resourceInfo.Uri,
                Metadata = resourceInfo.Metadata
            };
            attempt.Resources.Add(resource);
            await attempt.SaveChangesAsync(ct);
            // Untracked while the chunks go in, or each would be added to its Chunks collection and kept.
            attempt.Entry(resource).State = EntityState.Detached;

            // Checked again under the lock below, like create-only; this only spares reading the content of an upload
            // that will fail. After the insert, so a missing container is reported rather than unmet conditions.
            if (conditions is not null)
                await ThrowIfNotMetAsync(attempt, conditions, resourceContainerName, resourceInfo.Name, ct);

            contentRead = true;
            using var content = new HashingStream(stream);
            var batch = new List<ResourceChunkModel>(ChunksPerUploadBatch);
            // Each batch is stored before the next is read, so the batch's buffers can be reused for it.
            await foreach (ResourceChunkModel chunk in ResourceChunker.ChunksAsync(content, ChunksPerUploadBatch, ct))
            {
                chunk.ResourceId = resource.Id;
                batch.Add(chunk);
                if (batch.Count == ChunksPerUploadBatch) await SaveChunksAsync(attempt, batch, ct);
            }
            await SaveChunksAsync(attempt, batch, ct);

            // The in-memory provider the unit tests use has no locks.
            if (attempt.Database.IsRelational())
                await LockNameAsync(attempt, resourceContainerName, resourceInfo.Name, ct);

            List<ResourceModel> priorLatest = await attempt.Resources
                .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceInfo.Name && r.IsLatest)
                .ToListAsync(ct);
            if (createOnly && priorLatest.Count > 0)
                throw new ResourceConflictException($"Resource with name {resourceInfo.Name} already exists.");
            if (conditions is not null)
            {
                ResourceModel? current = priorLatest.FirstOrDefault();
                ThrowIfNotMet(conditions, resourceInfo.Name, current?.VersionId, current?.ContentHash);
            }

            // Stamped under the name's lock as the version becomes current, not before its content was read. A wrong
            // declared length throws here, before anything about the name changes.
            written = content.Complete(resourceInfo.ContentLength, _clock.GetUtcNow());

            // Clear the old current version before setting the new one, so the unique "latest" index never holds
            // two rows for the name in the middle of the write.
            foreach (ResourceModel prior in priorLatest) prior.IsLatest = false;
            await attempt.SaveChangesAsync(ct);

            attempt.Attach(resource);
            resource.IsLatest = true;
            resource.ContentLength = written.ContentLength;
            resource.ContentHash = written.ContentHash;
            resource.LastModified = written.LastModified;
            await attempt.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);
        }

        try
        {
            using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
            await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
            {
                try
                {
                    await AttemptAsync(ct);
                }
                catch (Exception exception)
                {
                    // Kept for the refusal above, should the execution strategy retry after it.
                    lastFailure = exception;
                    throw;
                }
            }, cancellationToken);

            return written!;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: SqlForeignKeyViolation })
        {
            // Only a missing container is ours to report; any other foreign key stays a database error.
            using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
            if (await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken)) throw;

            throw new ResourceContainerNotFoundException($"Resource Container with the name {resourceContainerName} does not exist.", exception);
        }
    }

    /// <summary>Inserts a batch of chunks and stops tracking them, so the context doesn't keep the content.</summary>
    private static async Task SaveChunksAsync(ResourceDbContext dbContext, List<ResourceChunkModel> batch, CancellationToken cancellationToken)
    {
        dbContext.ResourceChunks.AddRange(batch);
        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (ResourceChunkModel chunk in batch) dbContext.Entry(chunk).State = EntityState.Detached;
        batch.Clear();
    }

    /// <summary>
    /// Runs a write to one resource name in a transaction holding that name's write lock, so writers of a name queue
    /// rather than race. Each attempt gets a fresh context so the execution strategy can retry all of it after a
    /// transient failure.
    /// </summary>
    private async Task<T> WriteNameAsync<T>(string resourceContainerName, string resourceName,
        Func<ResourceDbContext, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            using ResourceDbContext attempt = NewWriteContext();
            await using var transaction = await attempt.Database.BeginTransactionAsync(ct);

            // The in-memory provider the unit tests use has no locks.
            if (attempt.Database.IsRelational())
                await LockNameAsync(attempt, resourceContainerName, resourceName, ct);

            T result = await write(attempt, ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// A context for a write that runs in a transaction of its own. A failure anywhere in that transaction rolls all of it
    /// back, so the savepoint EF Core would otherwise set before each SaveChanges, a round trip apiece, is never used.
    /// </summary>
    private ResourceDbContext NewWriteContext()
    {
        ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        dbContext.Database.AutoSavepointsEnabled = false;
        return dbContext;
    }

    /// <summary>
    /// Takes an exclusive application lock on the resource name for the rest of the current transaction. One lock per
    /// name, rather than key-range locks on its rows, so writers can't deadlock by taking locks in different orders.
    /// Lock names are limited to 255 characters, so the name is hashed.
    /// </summary>
    private static async Task LockNameAsync(ResourceDbContext dbContext, string resourceContainerName, string resourceName, CancellationToken cancellationToken)
    {
        string lockName = "Resourcefull:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{resourceContainerName}\n{resourceName}")));

        await dbContext.Database.ExecuteSqlAsync($"""
            DECLARE @result int;
            EXEC @result = sp_getapplock @Resource = {lockName}, @LockMode = 'Exclusive', @LockOwner = 'Transaction';
            IF @result < 0 THROW 50000, 'Could not lock the resource name for writing.', 1;
            """, cancellationToken);
    }

    private static async Task ThrowIfNotMetAsync(ResourceDbContext dbContext, ResourceConditions conditions,
        string resourceContainerName, string resourceName, CancellationToken cancellationToken)
    {
        if (conditions.IfMatchVersionId is null && conditions.IfMatchContentHash is null) return;

        var current = await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.IsLatest)
            .Select(r => new { r.VersionId, r.ContentHash })
            .FirstOrDefaultAsync(cancellationToken);
        ThrowIfNotMet(conditions, resourceName, current?.VersionId, current?.ContentHash);
    }

    /// <summary>
    /// A <see langword="null"/> <paramref name="currentVersionId"/> means the name has no current version, which meets
    /// no condition; empty conditions are no conditions.
    /// </summary>
    private static void ThrowIfNotMet(ResourceConditions conditions, string resourceName, string? currentVersionId, string? currentContentHash)
    {
        if (conditions.IfMatchVersionId is null && conditions.IfMatchContentHash is null) return;

        if (currentVersionId is null
            || (conditions.IfMatchVersionId is { } id && id != currentVersionId)
            || (conditions.IfMatchContentHash is { } hash && hash != currentContentHash))
        {
            throw new ResourcePreconditionFailedException(currentVersionId is null
                ? $"Resource {resourceName} has no current version to meet the conditions."
                : $"Resource {resourceName}'s current version {currentVersionId} doesn't meet the conditions.",
                currentVersionId, currentContentHash);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation };

    public async Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.IsLatest)
            .Select(r => new ResourceInfo
            {
                ContainerName = r.ContainerName,
                Name = r.Name,
                VersionId = r.VersionId,
                LastModified = r.LastModified,
                IsLatest = r.IsLatest,
                ContentType = r.ContentType,
                ContentLength = r.ContentLength,
                ContentHash = r.ContentHash,
                Metadata = r.Metadata,
                Uri = r.Uri
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ResourceInfo>> GetResourcePageAsync(string resourceContainerName, string? prefix, string? afterName,
        int pageSize, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // A seek on the current-versions index, which holds (ContainerName, Name) in the names' binary order: the
        // prefix and the name to continue after bound the range, so a page reads only its own rows.
        IQueryable<ResourceModel> current = dbContext.Resources.Where(r => r.ContainerName == resourceContainerName && r.IsLatest);
        if (prefix is not null) current = current.Where(r => r.Name.StartsWith(prefix));
        if (afterName is not null) current = current.Where(r => string.Compare(r.Name, afterName) > 0);

        return await current
            .OrderBy(r => r.Name)
            .Take(pageSize)
            .Select(r => new ResourceInfo
            {
                ContainerName = r.ContainerName,
                Name = r.Name,
                VersionId = r.VersionId,
                LastModified = r.LastModified,
                IsLatest = r.IsLatest,
                ContentType = r.ContentType,
                ContentLength = r.ContentLength,
                ContentHash = r.ContentHash,
                Metadata = r.Metadata,
                Uri = r.Uri
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            .OrderByDescending(r => r.IsLatest)
            .ThenByDescending(r => r.Id)
            .Select(r => new ResourceInfo
            {
                ContainerName = r.ContainerName,
                Name = r.Name,
                VersionId = r.VersionId,
                LastModified = r.LastModified,
                IsLatest = r.IsLatest,
                ContentType = r.ContentType,
                ContentLength = r.ContentLength,
                ContentHash = r.ContentHash,
                Metadata = r.Metadata,
                Uri = r.Uri
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName,
        string? versionId = null, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        ResourceModel? existingResource = await ResolveAsync(dbContext.Resources, resourceContainerName, resourceName, versionId, cancellationToken);

        if (existingResource is null) return null;

        return new ResourceInfo
        {
            ContainerName = resourceContainerName,
            Name = existingResource.Name,
            VersionId = existingResource.VersionId,
            LastModified = existingResource.LastModified,
            IsLatest = existingResource.IsLatest,
            ContentType = existingResource.ContentType,
            ContentLength = existingResource.ContentLength,
            ContentHash = existingResource.ContentHash,
            Metadata = existingResource.Metadata,
            Uri = existingResource.Uri
        };
    }

    public Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) =>
        GetResourceStreamAsync(resourceContainerName, resourceName, versionId, 0, null, cancellationToken);

    public async Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId, long offset,
        long? length, CancellationToken cancellationToken = default)
    {
        // The stream reads the chunks through this context as the caller reads, and disposes it.
        ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        try
        {
            // One statement, so the length belongs to the version found even if it is deleted right after.
            var existingResource = await Version(dbContext.Resources, resourceContainerName, resourceName, versionId)
                .Select(r => new { r.Id, Length = r.Chunks!.Sum(c => (long?)c.Data.Length) ?? 0 })
                .FirstOrDefaultAsync(cancellationToken);
            if (existingResource == null)
            {
                await dbContext.DisposeAsync();
                return null;
            }

            long start = Math.Min(offset, existingResource.Length);
            long end = length < existingResource.Length - start ? start + length.Value : existingResource.Length;
            if (start == 0 && end == existingResource.Length)
                return new ResourceStream(PageReader(dbContext, existingResource.Id), existingResource.Length, dbContext);

            // Only the chunks the range overlaps, found by adding up their lengths: releases have stored different
            // chunk sizes, so a position can't be turned into an index by division.
            ChunkSpan chunks = (await dbContext.Database.SqlQuery<ChunkSpan>($"""
                SELECT MAX(CASE WHEN [End] <= {start} THEN [Index] END) AS [AfterIndex],
                    MAX(CASE WHEN [End] <= {start} THEN [End] END) AS [Start],
                    MIN(CASE WHEN [End] >= {end} THEN [Index] END) AS [ThroughIndex]
                FROM (SELECT [Index], SUM(CAST(DATALENGTH([Data]) AS bigint)) OVER (ORDER BY [Index] ROWS UNBOUNDED PRECEDING) AS [End]
                    FROM [ResourceChunks] WHERE [ResourceId] = {existingResource.Id}) AS [Chunks]
                """).ToListAsync(cancellationToken))[0];
            return new ResourceStream(PageReader(dbContext, existingResource.Id, chunks.AfterIndex, chunks.ThroughIndex),
                existingResource.Length, dbContext, chunks.Start ?? 0, start, end);
        }
        catch
        {
            await dbContext.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Reads a version's chunks a page at a time, each page its own short query, retried as a whole after a transient
    /// failure: every chunk, or for a range those after <paramref name="afterIndex"/> through
    /// <paramref name="throughIndex"/>. The data streams from the reader into buffers the pages share, rather than a new
    /// array per chunk, so a page's data is valid only until the next page is read.
    /// </summary>
    internal static Func<CancellationToken, Task<IReadOnlyList<ReadOnlyMemory<byte>>>> PageReader(ResourceDbContext dbContext,
        long resourceId, int? afterIndex = null, int? throughIndex = null)
    {
        // No assumption about where indexes start: the first release numbered chunks from zero.
        int? lastIndex = afterIndex;
        bool exhausted = false;
        var buffers = new byte[ChunksPerReadPage][];
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        return async cancellationToken =>
        {
            if (exhausted) return [];

            (List<ReadOnlyMemory<byte>> page, int? pageLastIndex) = await strategy.ExecuteAsync(async ct =>
            {
                var page = new List<ReadOnlyMemory<byte>>(ChunksPerReadPage);
                int? pageLastIndex = lastIndex;
                await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                try
                {
                    await using DbCommand command = dbContext.Database.GetDbConnection().CreateCommand();
                    command.CommandText =
                        $"SELECT TOP ({ChunksPerReadPage}) [Index], DATALENGTH([Data]), [Data] FROM [ResourceChunks] " +
                        "WHERE [ResourceId] = @resourceId AND (@lastIndex IS NULL OR [Index] > @lastIndex)" +
                        (throughIndex is null ? "" : " AND [Index] <= @throughIndex") + " ORDER BY [Index]";
                    command.Parameters.Add(new SqlParameter("@resourceId", resourceId));
                    command.Parameters.Add(new SqlParameter("@lastIndex", SqlDbType.Int) { Value = (object?)lastIndex ?? DBNull.Value });
                    if (throughIndex is not null)
                        command.Parameters.Add(new SqlParameter("@throughIndex", SqlDbType.Int) { Value = throughIndex });

                    // Sequential access streams each chunk's data instead of buffering the row first.
                    await using DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct)
                        .ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        pageLastIndex = reader.GetInt32(0);
                        int dataLength = (int)reader.GetInt64(1);
                        byte[] buffer = buffers[page.Count] is { } existing && existing.Length >= dataLength
                            ? existing
                            : buffers[page.Count] = new byte[Math.Max(dataLength, ResourceChunker.ChunkSize)];
                        await using Stream data = reader.GetStream(2);
                        try
                        {
                            await data.ReadExactlyAsync(buffer.AsMemory(0, dataLength), ct).ConfigureAwait(false);
                        }
                        catch (IOException exception) when (exception.InnerException is SqlException sqlException)
                        {
                            // The column stream wraps a failure to read from the server; unwrapped, the execution
                            // strategy can recognize a transient one and read the page again, and a caller doesn't
                            // mistake it for content cut short by a delete.
                            ExceptionDispatchInfo.Throw(sqlException);
                        }
                        page.Add(buffer.AsMemory(0, dataLength));
                    }

                    // What the server sends after the rows (its messages, or an error) reaches the connection only if
                    // the reader moves past them; disposing it drops them. EF Core reads its own results to the end too.
                    await reader.NextResultAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
                }

                return (page, pageLastIndex);
            }, cancellationToken).ConfigureAwait(false);

            exhausted = page.Count < ChunksPerReadPage || pageLastIndex == throughIndex;
            lastIndex = pageLastIndex;
            return page;
        };
    }

    public Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) =>
        DeleteResourceAsync(resourceContainerName, resourceName, versionId, new ResourceConditions(), cancellationToken);

    public Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId,
        ResourceConditions conditions, CancellationToken cancellationToken = default)
    {
        bool deletedInAnEarlierAttempt = false;

        // Under the name's lock, so a concurrent delete of the same version finds it gone instead of failing, the
        // promotion of the next version can't race a write, and the conditions hold until the delete commits.
        return WriteNameAsync(resourceContainerName, resourceName, async (dbContext, ct) =>
        {
            // A retry after a commit whose acknowledgement was lost finds what it deleted gone. The conditions it met
            // no longer hold then, and it mustn't report that nothing was deleted.
            if (deletedInAnEarlierAttempt && !await dbContext.Resources.AnyAsync(r => r.ContainerName == resourceContainerName
                && r.Name == resourceName && (versionId == null || r.VersionId == versionId), ct))
                return true;

            await ThrowIfNotMetAsync(dbContext, conditions, resourceContainerName, resourceName, ct);
            deletedInAnEarlierAttempt = await DeleteVersionsAsync(dbContext, ct);
            return deletedInAnEarlierAttempt;
        }, cancellationToken);

        async Task<bool> DeleteVersionsAsync(ResourceDbContext dbContext, CancellationToken ct)
        {
            if (versionId == null)
            {
                // Without a version, every version of the name goes; their chunks cascade.
                List<ResourceModel> versions = await dbContext.Resources
                    .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
                    .ToListAsync(ct);
                dbContext.Resources.RemoveRange(versions);
                return await dbContext.SaveChangesAsync(ct) > 0;
            }

            ResourceModel? existingResource = await ResolveAsync(dbContext.Resources, resourceContainerName, resourceName, versionId, ct);

            if (existingResource == null) return false;

            dbContext.Resources.Remove(existingResource);

            if (existingResource.IsLatest)
            {
                ResourceModel? next = await dbContext.Resources
                    .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.Id != existingResource.Id)
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync(ct);
                if (next != null) next.IsLatest = true;
            }

            return await dbContext.SaveChangesAsync(ct) > 0;
        }
    }

    public async Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        var resourceContainerModel = new ResourceContainerModel
        {
            Name = resourceContainerInfo.Name,
            Uri = resourceContainerInfo.Uri,
            Metadata = resourceContainerInfo.Metadata
        };
        dbContext.ResourceContainers.Add(resourceContainerModel);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new ResourceConflictException($"Resource Container with the name {resourceContainerInfo.Name} already exists.", exception);
        }
    }

    public async Task<List<ResourceContainerInfo>> GetAllResourceContainerInfoAsync(CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.ResourceContainers.Select(x => new ResourceContainerInfo
        {
            Name = x.Name,
            Uri = x.Uri,
            Metadata = x.Metadata
        }).ToListAsync(cancellationToken);
    }

    public async Task<ResourceContainerInfo?> GetResourceContainerInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.ResourceContainers.Where(x => x.Name == resourceContainerName).Select(x => new ResourceContainerInfo
        {
            Name = x.Name,
            Uri = x.Uri,
            Metadata = x.Metadata
        }).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // One aggregate over the container's version rows, from their recorded lengths. Only a version without one, as
        // stored by an early release, has its chunks' lengths added up instead.
        UsageRow usage = (await dbContext.Database.SqlQuery<UsageRow>($"""
            SELECT COALESCE(SUM(COALESCE(r.[ContentLength], c.[Length])), 0) AS [StoredSize],
                COUNT_BIG(CASE WHEN r.[IsLatest] = 1 THEN 1 END) AS [ResourceCount],
                COUNT_BIG(*) AS [VersionCount]
            FROM [Resources] r
            OUTER APPLY (SELECT SUM(CAST(DATALENGTH(ch.[Data]) AS bigint)) AS [Length] FROM [ResourceChunks] ch
                WHERE ch.[ResourceId] = r.[Id] AND r.[ContentLength] IS NULL) c
            WHERE r.[ContainerName] = {resourceContainerName}
            """).ToListAsync(cancellationToken))[0];
        return new ResourceContainerUsage(usage.StoredSize, usage.ResourceCount, usage.VersionCount);
    }

    public async Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        if (!await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken)) return false;

        // Each batch commits on its own, so no single transaction holds the whole container's content. A batch takes
        // whole resources, each with its chunks, so a reader never sees part of one, until it reaches the chunk budget.
        // Each pass removes only what earlier passes left, so a delete that is cancelled or fails partway is finished
        // by calling it again.
        while (true)
        {
            var candidates = await dbContext.Resources
                .Where(r => r.ContainerName == resourceContainerName)
                .OrderBy(r => r.Name).ThenBy(r => r.VersionId)
                .Select(r => new { r.Id, r.Name, r.IsLatest, Chunks = r.Chunks!.Count })
                .Take(MaxResourcesPerDeleteBatch)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0) break;

            // A name's current version goes after all its others: deleting it first would leave the name with stored
            // versions but none current. Candidates come grouped by name, so a current version waits while more of its
            // name follow it, or while the list was cut short and more might.
            bool cutShort = candidates.Count == MaxResourcesPerDeleteBatch;
            var batch = new List<long>();
            long chunks = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                bool moreOfName = i + 1 < candidates.Count ? candidates[i + 1].Name == candidate.Name : cutShort;
                if (candidate.IsLatest && moreOfName) continue;
                if (batch.Count > 0 && chunks + candidate.Chunks > ChunksPerDeleteBatch) break;
                batch.Add(candidate.Id);
                chunks += candidate.Chunks;
            }

            dbContext.Resources.RemoveRange(await dbContext.Resources.Where(r => batch.Contains(r.Id)).ToListAsync(cancellationToken));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        ResourceContainerModel? container = await dbContext.ResourceContainers
            .FirstOrDefaultAsync(x => x.Name == resourceContainerName, cancellationToken);
        if (container == null) return false;

        dbContext.ResourceContainers.Remove(container);
        return await dbContext.SaveChangesAsync(cancellationToken) > 0;
    }

    private static Task<ResourceModel?> ResolveAsync(IQueryable<ResourceModel> query,
        string resourceContainerName, string resourceName, string? versionId, CancellationToken cancellationToken) =>
        Version(query, resourceContainerName, resourceName, versionId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>The named version, or the current one when <paramref name="versionId"/> is null.</summary>
    private static IQueryable<ResourceModel> Version(IQueryable<ResourceModel> query, string resourceContainerName, string resourceName, string? versionId)
    {
        query = query.Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName);

        return versionId == null
            ? query.Where(r => r.IsLatest)
            : query.Where(r => r.VersionId == versionId);
    }

    /// <summary>What <see cref="GetContainerUsageAsync"/> reads, named as its query's columns.</summary>
    internal sealed class UsageRow
    {
        public long StoredSize { get; set; }
        public long ResourceCount { get; set; }
        public long VersionCount { get; set; }
    }

    /// <summary>
    /// The chunks a range overlaps: those after <see cref="AfterIndex"/> (from the first, when null) through
    /// <see cref="ThroughIndex"/>, the first of them starting at <see cref="Start"/> in the content (0, when null).
    /// </summary>
    internal sealed class ChunkSpan
    {
        public int? AfterIndex { get; set; }
        public long? Start { get; set; }
        public int? ThroughIndex { get; set; }
    }
}
