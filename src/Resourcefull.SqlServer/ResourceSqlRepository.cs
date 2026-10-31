using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.Utilities;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
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

    // At least 1 MiB of content per round trip while uploading and downloading.
    internal const int ChunksPerUploadBatch = (1024 * 1024 + ResourceChunker.ChunkSize - 1) / ResourceChunker.ChunkSize;
    private const int ChunksPerReadPage = ChunksPerUploadBatch;

    /// <summary>
    /// Caps the content one batch of a container or name delete, or of an expiry sweep, removes, counted in chunks, so no
    /// single transaction has to log a whole container or a name's whole history. A resource bigger than the cap still
    /// goes in a batch of its own.
    /// </summary>
    internal int ChunksPerDeleteBatch { get; init; } = 256;

    /// <summary>
    /// How many chunks one statement deletes. SQL Server locks the whole chunk table, holding up every upload, once a
    /// statement holds 5,000 locks on the chunks' rows: one per chunk, since the locks on their content pages don't count
    /// toward it. A version with more chunks than this has them deleted this many at a time before it goes; a smaller one
    /// takes them with it.
    /// </summary>
    internal const int ChunksPerDeleteStatement = 1_000;

    /// <summary>
    /// The error a name's lock raises when SQL Server chooses it as a deadlock victim, or it times out: transient, like a
    /// deadlock (1205), so the retry policy <c>UseSqlServer</c> sets retries the write.
    /// </summary>
    internal const int NameLockNotGrantedError = 50001;

    /// <summary>
    /// How often an upload moves its staged version's <see cref="ResourceModel.StagedAt"/> forward, as a chunk arrives:
    /// well within the hour after which the expiry sweep takes a staged version as abandoned, so an upload is safe as
    /// long as a chunk, about 40 KB, arrives every 50 minutes, far slower than Kestrel lets a request body go.
    /// </summary>
    private static readonly TimeSpan StagedStampInterval = TimeSpan.FromMinutes(10);

    public Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default) =>
        AddResourceAsync(resourceContainerName, resourceInfo, stream, new ResourceWriteCondition { CreateOnly = createOnly }, cancellationToken);

    public Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream,
        ResourceWriteCondition condition, CancellationToken cancellationToken = default) =>
        AddVersionAsync(resourceContainerName, resourceInfo, stream, condition, null, cancellationToken);

    public Task<WrittenContent> ImportVersionAsync(string resourceContainerName, ResourceInfo version, Stream content,
        CancellationToken cancellationToken = default) =>
        AddVersionAsync(resourceContainerName, version, content, new ResourceWriteCondition(), version.LastModified, cancellationToken);

    /// <summary>
    /// Stores a new version as the name's current one: an upload, stamped with the time it becomes current, or an
    /// import, stamped with <paramref name="lastModified"/>, the time it was first stored.
    /// </summary>
    /// <remarks>
    /// It's stored in three steps, so no transaction, lock or connection is held while the content arrives, however
    /// slowly it does:
    /// <list type="number">
    /// <item>A short transaction inserts the version, staged, so nothing else sees it.</item>
    /// <item>
    /// The content goes in a batch at a time, each committed on its own and, after a transient failure, sent again on
    /// its own from the bytes it holds, so even content that can't be read twice survives one.
    /// </item>
    /// <item>A short transaction under the name's lock checks the conditions and makes the version current.</item>
    /// </list>
    /// A version that isn't finished is deleted, or, should that fail too, left for the sweep of abandoned uploads.
    /// </remarks>
    private async Task<WrittenContent> AddVersionAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream,
        ResourceWriteCondition condition, DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        ResourceModel? staged = null;
        bool stored = false;
        try
        {
            staged = await StartVersionAsync(strategy, resourceContainerName, resourceInfo, condition, cancellationToken);
            using var content = new HashingStream(stream);
            int batches = await StoreContentAsync(dbContext, strategy, staged.Id, content, cancellationToken);
            // On the client's span only, never an application's own; added to, so an import's counts every version's.
            if (Activity.Current is { } activity && activity.Source.Name == ResourcefullTelemetry.ActivitySourceName)
            {
                int before = activity.GetTagItem("resourcefull.batches") as int? ?? 0;
                activity.SetTag("resourcefull.batches", before + batches);
            }
            WrittenContent written = await FinishVersionAsync(resourceContainerName, resourceInfo, staged, content,
                condition, lastModified, cancellationToken);
            stored = true;
            return written;
        }
        catch (Exception exception) when (IsForeignKeyViolation(exception) || exception is DbUpdateConcurrencyException)
        {
            // Its container was deleted meanwhile, taking the staged version with it; or the staged version alone is
            // gone, as the sweep of abandoned uploads takes one whose upload stored nothing for an hour.
            await ThrowIfContainerMissingAsync(resourceContainerName, exception, cancellationToken);
            if (staged is null) throw;
            throw new InvalidOperationException($"The resource {resourceInfo.Name} wasn't stored in the container " +
                $"{resourceContainerName}: its version was deleted before it was complete.", exception);
        }
        finally
        {
            if (!stored && staged is not null)
            {
                try
                {
                    await DeleteStagedVersionAsync(strategy, staged.Id, null, CancellationToken.None);
                }
                catch (Exception)
                {
                    // The upload's own failure is the one to report; the sweep of abandoned uploads deletes this later.
                }
            }
        }
    }

    private static bool IsForeignKeyViolation(Exception exception) =>
        (exception as SqlException ?? exception.InnerException as SqlException) is { Number: SqlForeignKeyViolation };

    /// <summary>
    /// Inserts the version, staged, after the checks that spare reading the content of an upload that would fail, and
    /// returns it, untracked. Without checks the insert is a statement of its own, which commits as it runs; with them,
    /// the checks and the insert share a transaction.
    /// </summary>
    private async Task<ResourceModel> StartVersionAsync(IExecutionStrategy strategy, string resourceContainerName,
        ResourceInfo resourceInfo, ResourceWriteCondition condition, CancellationToken cancellationToken)
    {
        bool committing = false;
        return await strategy.ExecuteAsync(async ct =>
        {
            using ResourceDbContext attempt = NewWriteContext();

            // A retry after a commit whose acknowledgement was lost finds its own version staged. A first attempt on SQL
            // Server skips the look, a round trip on every write: the insert below is refused if the name already has the
            // version. The in-memory provider the unit tests use doesn't enforce the unique index.
            if (committing || !attempt.Database.IsRelational())
            {
                ResourceModel? existing = await attempt.Resources.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ContainerName == resourceContainerName && r.Name == resourceInfo.Name
                        && r.VersionId == resourceInfo.VersionId, ct);
                if (existing is not null)
                {
                    // Its own only while staged: a stored version with the ID, which an import names, is another's.
                    if (committing && existing.StagedAt is not null) return existing;
                    throw VersionExists(resourceContainerName, resourceInfo.Name, resourceInfo.VersionId);
                }
            }

            // Conditions are checked after the insert, so a version that fails them mustn't stay; create-only is
            // checked before it, so needs no transaction.
            await using IDbContextTransaction? transaction = condition.Conditions is not null
                ? await attempt.Database.BeginTransactionAsync(ct)
                : null;

            // Checked again as the version becomes current; this only spares reading the content of an upload that will
            // fail.
            if (condition.CreateOnly && await attempt.Resources.AnyAsync(r => r.ContainerName == resourceContainerName
                && r.Name == resourceInfo.Name && r.IsLatest, ct))
                throw ResourceExists(resourceContainerName, resourceInfo.Name);

            // Inserted first, so a missing container is reported before any content is read.
            ResourceModel resource = NewVersion(resourceContainerName, resourceInfo);
            resource.StagedAt = _clock.GetUtcNow();
            attempt.Resources.Add(resource);
            // Without a transaction, the insert commits as it runs.
            committing = transaction is null;
            try
            {
                await attempt.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                throw VersionExists(resourceContainerName, resourceInfo.Name, resourceInfo.VersionId, exception);
            }

            // Checked again as the version becomes current, like create-only. After the insert, so a missing container
            // is reported rather than unmet conditions.
            if (condition.Conditions is { } conditions)
                await ThrowIfNotMetAsync(attempt, conditions, resourceContainerName, resourceInfo.Name, ct);

            if (transaction is not null)
            {
                committing = true;
                await transaction.CommitAsync(ct);
            }
            attempt.Entry(resource).State = EntityState.Detached;
            return resource;
        }, cancellationToken);
    }

    /// <summary>
    /// Stores the content's chunks a batch at a time, so memory stays bounded whatever its size, each batch committed on
    /// its own. Every few minutes a batch also moves the version's <see cref="ResourceModel.StagedAt"/> forward, and goes
    /// with what it has, however little, so the sweep of abandoned uploads leaves a slow upload alone.
    /// </summary>
    /// <returns>How many batches it stored.</returns>
    private async Task<int> StoreContentAsync(ResourceDbContext dbContext, IExecutionStrategy strategy, long resourceId,
        Stream content, CancellationToken cancellationToken)
    {
        int batches = 0;
        DateTimeOffset lastStamped = _clock.GetUtcNow();
        var batch = new List<ResourceChunkModel>(ChunksPerUploadBatch);
        // Each batch is stored before the chunker reuses its buffers: it shares ChunksPerUploadBatch of them in turn,
        // and a batch holds at most that many chunks, however early it goes.
        await foreach (ResourceChunkModel chunk in
            ResourceChunker.ChunksAsync(content, ChunksPerUploadBatch, cancellationToken))
        {
            chunk.ResourceId = resourceId;
            batch.Add(chunk);
            bool stamp = _clock.GetUtcNow() - lastStamped >= StagedStampInterval;
            if (batch.Count < ChunksPerUploadBatch && !stamp) continue;

            await StoreBatchAsync(dbContext, strategy, batch, stamp, cancellationToken);
            batches++;
            if (stamp) lastStamped = _clock.GetUtcNow();
        }
        if (batch.Count == 0) return batches;
        await StoreBatchAsync(dbContext, strategy, batch, stamp: false, cancellationToken);
        return batches + 1;
    }

    /// <summary>
    /// Makes the staged version its name's current one, under the name's lock, once the conditions and a declared length
    /// hold, and returns what was stored.
    /// </summary>
    private Task<WrittenContent> FinishVersionAsync(string resourceContainerName, ResourceInfo resourceInfo,
        ResourceModel staged, HashingStream content, ResourceWriteCondition condition, DateTimeOffset? lastModified,
        CancellationToken cancellationToken)
    {
        WrittenContent? written = null;
        return WriteNameAsync(resourceContainerName, resourceInfo.Name, async (attempt, ct) =>
        {
            // A retry after a commit whose acknowledgement was lost finds it current, which the filter lets it see.
            if (written is not null && await attempt.Resources.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == staged.Id, ct) is { } stored)
            {
                return new WrittenContent(stored.ContentLength!.Value, stored.ContentHash!, stored.LastModified);
            }

            List<ResourceModel> priorLatest =
                await CurrentForWriteAsync(attempt, resourceContainerName, resourceInfo.Name, condition, ct);

            // Stamped under the name's lock as the version becomes current, not before its content was read. A wrong
            // declared length throws here, before anything about the name changes.
            WrittenContent completed = content.Complete(resourceInfo.ContentLength, lastModified ?? _clock.GetUtcNow());

            // Attached as it was staged, whatever an earlier attempt changed, so only what changes now is saved. Should
            // it be gone, the save throws DbUpdateConcurrencyException, which the caller reports.
            ResourceModel version = attempt.Resources.Attach(new ResourceModel
            {
                Id = staged.Id, ContainerName = staged.ContainerName, Name = staged.Name, StagedAt = staged.StagedAt
            }).Entity;
            version.StagedAt = null;
            await MakeCurrentAsync(attempt, priorLatest, version, completed, ct);
            return written = completed;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a version and its content if it's still staged, and, given <paramref name="staleBefore"/>, has been since
    /// before then. Its chunks go a statement at a time when there are many, as any version's do.
    /// </summary>
    /// <returns>Whether it deleted the version.</returns>
    private async Task<bool> DeleteStagedVersionAsync(IExecutionStrategy strategy, long resourceId,
        DateTimeOffset? staleBefore, CancellationToken cancellationToken)
    {
        bool deletedInAnEarlierAttempt = false;
        return await strategy.ExecuteAsync(async ct =>
        {
            using ResourceDbContext attempt = NewWriteContext();
            await using var transaction = await attempt.Database.BeginTransactionAsync(ct);

            // Locked as it's read, so an upload can't make it current, or move its stamp forward, before it goes. A retry
            // after a commit whose acknowledgement was lost finds it gone.
            List<int> chunks = attempt.Database.IsRelational()
                ? await attempt.Database.SqlQuery<int>($"""
                    SELECT (SELECT COUNT(*) FROM [ResourceChunks] c WHERE c.[ResourceId] = r.[Id]) AS [Value]
                    FROM [Resources] r WITH (UPDLOCK, ROWLOCK)
                    WHERE r.[Id] = {resourceId} AND r.[StagedAt] IS NOT NULL
                        AND ({staleBefore} IS NULL OR r.[StagedAt] < {staleBefore})
                    """).ToListAsync(ct)
                : await attempt.Resources.IgnoreQueryFilters()
                    .Where(r => r.Id == resourceId && r.StagedAt != null
                        && (staleBefore == null || r.StagedAt < staleBefore))
                    .Select(r => r.Chunks!.Count)
                    .ToListAsync(ct);
            if (chunks.Count == 0) return deletedInAnEarlierAttempt;

            await DeleteLargeVersionsChunksAsync(attempt, [(resourceId, chunks[0])], ct);
            attempt.Resources.Remove(attempt.Resources.Attach(
                new ResourceModel { Id = resourceId, ContainerName = "", Name = "" }).Entity);
            await attempt.SaveChangesAsync(ct);
            deletedInAnEarlierAttempt = true;
            await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);
    }

    public async Task<int> DeleteAbandonedUploadsAsync(DateTimeOffset staleBefore,
        CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        int deleted = 0;
        var failures = new List<Exception>();
        long afterId = 0;
        while (true)
        {
            // A seek on the index of staged versions, so a sweep that finds none reads nothing else. Each page starts
            // after the last, so versions that fail to delete can't hold up the rest.
            List<long> abandoned = await dbContext.Resources.IgnoreQueryFilters()
                .Where(r => r.StagedAt != null && r.StagedAt < staleBefore && r.Id > afterId)
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .Take(MaxResourcesPerDeleteBatch)
                .ToListAsync(cancellationToken);
            foreach (long resourceId in abandoned)
            {
                try
                {
                    if (await DeleteStagedVersionAsync(strategy, resourceId, staleBefore, cancellationToken)) deleted++;
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    failures.Add(exception);
                }
            }
            if (abandoned.Count < MaxResourcesPerDeleteBatch) break;
            afterId = abandoned[^1];
        }

        SqlTelemetry.CountAbandonedUploads(deleted);
        if (failures.Count > 0)
        {
            throw new AggregateException($"{failures.Count} abandoned uploads' versions weren't deleted, and stay for " +
                $"the next sweep; {deleted} were.", failures);
        }
        return deleted;
    }

    /// <summary>A version about to be stored, not yet current, stamped now.</summary>
    private ResourceModel NewVersion(string resourceContainerName, ResourceInfo resourceInfo) => new()
    {
        ContainerName = resourceContainerName,
        Name = resourceInfo.Name,
        VersionId = resourceInfo.VersionId,
        LastModified = _clock.GetUtcNow(),
        IsLatest = false,
        ContentType = resourceInfo.ContentType,
        Uri = resourceInfo.Uri,
        Metadata = resourceInfo.Metadata,
        CopiedFromContainerName = resourceInfo.CopiedFrom?.ContainerName,
        CopiedFromName = resourceInfo.CopiedFrom?.ResourceName,
        CopiedFromVersionId = resourceInfo.CopiedFrom?.VersionId,
        ExpiresAt = resourceInfo.ExpiresAt
    };

    /// <summary>
    /// Throws <see cref="ResourceContainerNotFoundException"/> for a foreign key violation a missing container caused.
    /// Only a missing container is ours to report; the caller rethrows any other violation as the database error it is.
    /// </summary>
    private async Task ThrowIfContainerMissingAsync(string resourceContainerName, Exception exception, CancellationToken cancellationToken)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        if (!await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken))
        {
            throw new ResourceContainerNotFoundException($"The container {resourceContainerName} doesn't exist.",
                exception)
            {
                ContainerName = resourceContainerName
            };
        }
    }

    public async Task<WrittenContent?> CopyResourceAsync(ResourceLocation source, string? sourceVersionId, ResourceInfo destination,
        ResourceWriteCondition condition, CancellationToken cancellationToken = default)
    {
        // A version stored before hashes were kept has none to copy, so its content is read, and hashed, as an upload's is.
        using (ResourceDbContext dbContext = dbContextFactory.CreateDbContext())
        {
            var found = await Version(dbContext.Resources, source.ContainerName, source.ResourceName, sourceVersionId)
                .Select(r => new { r.ContentHash })
                .FirstOrDefaultAsync(cancellationToken);
            if (found is null) return null;
            if (found.ContentHash is null)
            {
                await using Stream? content = await GetResourceStreamAsync(source.ContainerName, source.ResourceName, sourceVersionId, cancellationToken);
                return content is null ? null : await AddResourceAsync(destination.ContainerName, destination, content, condition, cancellationToken);
            }
        }

        WrittenContent? written = null;
        try
        {
            return await WriteNamesAsync([source, new ResourceLocation(destination.ContainerName, destination.Name)], async (attempt, ct) =>
            {
                // A retry after a commit whose acknowledgement was lost finds the copy already stored.
                if (await attempt.Resources.AnyAsync(r => r.ContainerName == destination.ContainerName && r.Name == destination.Name
                    && r.VersionId == destination.VersionId, ct))
                {
                    return written
                        ?? throw VersionExists(destination.ContainerName, destination.Name, destination.VersionId);
                }

                // Read under the source name's lock, so the version can't be deleted while its chunks are copied.
                var from = await Version(attempt.Resources, source.ContainerName, source.ResourceName, sourceVersionId)
                    .Select(r => new { r.Id, r.ContentHash, Length = r.Chunks!.Sum(c => (long?)c.Data.Length) ?? 0 })
                    .FirstOrDefaultAsync(ct);
                if (from is null) return null;

                List<ResourceModel> priorLatest =
                    await CurrentForWriteAsync(attempt, destination.ContainerName, destination.Name, condition, ct);

                ResourceModel copy = NewVersion(destination.ContainerName, destination);
                attempt.Resources.Add(copy);
                await attempt.SaveChangesAsync(ct);
                // Copied by the server, so no content passes through the application.
                await attempt.Database.ExecuteSqlAsync(
                    $"INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data]) SELECT {copy.Id}, [Index], [Data] FROM [ResourceChunks] WHERE [ResourceId] = {from.Id}",
                    ct);

                var stored = new WrittenContent(from.Length, from.ContentHash!, _clock.GetUtcNow());
                await MakeCurrentAsync(attempt, priorLatest, copy, stored, ct);
                return written = stored;
            }, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: SqlForeignKeyViolation })
        {
            await ThrowIfContainerMissingAsync(destination.ContainerName, exception, cancellationToken);
            throw;
        }
    }

    public async Task<bool> MoveResourceAsync(ResourceLocation source, ResourceLocation destination, ResourceConditions? sourceConditions,
        CancellationToken cancellationToken = default)
    {
        bool moved = false;
        try
        {
            return await WriteNamesAsync([source, destination], async (attempt, ct) =>
            {
                ResourceModel? current = await attempt.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.ContainerName == source.ContainerName
                    && r.Name == source.ResourceName && r.IsLatest, ct);
                // A retry after a commit whose acknowledgement was lost finds the resource already moved.
                if (current is null) return moved;
                if (sourceConditions is not null)
                {
                    ThrowIfNotMet(sourceConditions, source.ContainerName, source.ResourceName, current.VersionId,
                        current.ContentHash);
                }
                if (await attempt.Resources.AnyAsync(r => r.ContainerName == destination.ContainerName && r.Name == destination.ResourceName, ct))
                    throw ResourceExists(destination.ContainerName, destination.ResourceName);

                // Only the versions' container and name change: their chunks hang off their IDs, so no content is copied.
                // The stored URI, which only 2.x reads, would name the old place, so it's cleared rather than left wrong.
                await attempt.Resources
                    .Where(r => r.ContainerName == source.ContainerName && r.Name == source.ResourceName)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.ContainerName, destination.ContainerName)
                        .SetProperty(r => r.Name, destination.ResourceName)
                        .SetProperty(r => r.Uri, (string?)null), ct);
                return moved = true;
            }, cancellationToken);
        }
        catch (Exception exception) when ((exception as SqlException ?? exception.InnerException as SqlException)?.Number == SqlForeignKeyViolation)
        {
            await ThrowIfContainerMissingAsync(destination.ContainerName, exception, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// The name's current version, read under the name's lock, for a write that will replace it: a create-only write to
    /// a name that has one throws <see cref="ResourceConflictException"/>, and conditions that don't hold throw
    /// <see cref="ResourcePreconditionFailedException"/>.
    /// </summary>
    private static async Task<List<ResourceModel>> CurrentForWriteAsync(ResourceDbContext attempt,
        string resourceContainerName, string resourceName, ResourceWriteCondition condition,
        CancellationToken cancellationToken)
    {
        List<ResourceModel> priorLatest = await attempt.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.IsLatest)
            .ToListAsync(cancellationToken);
        if (condition.CreateOnly && priorLatest.Count > 0)
            throw ResourceExists(resourceContainerName, resourceName);
        if (condition.Conditions is { } conditions)
        {
            ResourceModel? current = priorLatest.FirstOrDefault();
            ThrowIfNotMet(conditions, resourceContainerName, resourceName, current?.VersionId, current?.ContentHash);
        }
        return priorLatest;
    }

    /// <summary>
    /// Makes <paramref name="version"/> its name's current version, with the length, hash and time
    /// <paramref name="written"/> gives. The old current version is cleared and saved first, so the unique "latest"
    /// index never holds two rows for the name in the middle of the write.
    /// </summary>
    private static async Task MakeCurrentAsync(ResourceDbContext attempt, List<ResourceModel> priorLatest,
        ResourceModel version, WrittenContent written, CancellationToken cancellationToken)
    {
        foreach (ResourceModel prior in priorLatest) prior.IsLatest = false;
        await attempt.SaveChangesAsync(cancellationToken);

        version.IsLatest = true;
        version.ContentLength = written.ContentLength;
        version.ContentHash = written.ContentHash;
        version.LastModified = written.LastModified;
        await attempt.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a batch of chunks in one statement, committed as it runs, and empties the batch; with
    /// <paramref name="stamp"/>, the same round trip moves the version's <see cref="ResourceModel.StagedAt"/> forward.
    /// After a transient failure the strategy sends the batch again from the same buffers, and a retry after a commit
    /// whose acknowledgement was lost, refused because the chunks are already stored, has nothing left to do. The
    /// command is sent straight through SqlClient: tracking each chunk, or sending the command through EF Core's raw SQL,
    /// more than doubled what an upload allocated.
    /// </summary>
    private async Task StoreBatchAsync(ResourceDbContext dbContext, IExecutionStrategy strategy,
        List<ResourceChunkModel> batch, bool stamp, CancellationToken cancellationToken)
    {
        bool sentBefore = false;
        await strategy.ExecuteAsync(async ct =>
        {
            bool retry = sentBefore;
            sentBefore = true;
            long resourceId = batch[0].ResourceId;
            DateTimeOffset now = _clock.GetUtcNow();

            if (!dbContext.Database.IsRelational())
            {
                dbContext.ResourceChunks.AddRange(batch);
                if (stamp)
                {
                    (await dbContext.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId, ct)).StagedAt =
                        now;
                }
                await dbContext.SaveChangesAsync(ct);
                dbContext.ChangeTracker.Clear();
                return;
            }

            await dbContext.Database.OpenConnectionAsync(ct);
            try
            {
                await using DbCommand command = NewCommand(dbContext);
                var sql = new StringBuilder("INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data]) VALUES ");
                command.Parameters.Add(new SqlParameter("@resourceId", resourceId));
                for (int i = 0; i < batch.Count; i++)
                {
                    sql.Append(i == 0 ? "" : ", ").Append($"(@resourceId, @index{i}, @data{i})");
                    command.Parameters.Add(new SqlParameter($"@index{i}", batch[i].Index));
                    command.Parameters.Add(
                        new SqlParameter($"@data{i}", SqlDbType.VarBinary, -1) { Value = batch[i].Data });
                }
                if (stamp)
                {
                    sql.Append("; UPDATE [Resources] SET [StagedAt] = @now WHERE [Id] = @resourceId");
                    command.Parameters.Add(new SqlParameter("@now", now));
                }
                command.CommandText = sql.ToString();
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException exception)
                when (retry && exception.Number is SqlUniqueConstraintViolation or SqlUniqueIndexViolation)
            {
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }, cancellationToken);
        batch.Clear();
    }

    private Task<T> WriteNameAsync<T>(string resourceContainerName, string resourceName,
        Func<ResourceDbContext, CancellationToken, Task<T>> write, CancellationToken cancellationToken) =>
        WriteNamesAsync([new ResourceLocation(resourceContainerName, resourceName)], write, cancellationToken);

    /// <summary>
    /// Runs a write to resource names in a transaction holding each name's write lock, so writers of a name queue
    /// rather than race. The locks are taken in one order whatever the order of the names, so two writes to the same
    /// two names can't deadlock. Each attempt gets a fresh context so the execution strategy can retry all of it after a
    /// transient failure.
    /// </summary>
    private async Task<T> WriteNamesAsync<T>(IEnumerable<ResourceLocation> names,
        Func<ResourceDbContext, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        List<ResourceLocation> ordered = [.. names.Distinct()
            .OrderBy(n => n.ContainerName, StringComparer.Ordinal).ThenBy(n => n.ResourceName, StringComparer.Ordinal)];

        return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            using ResourceDbContext attempt = NewWriteContext();
            await using var transaction = await attempt.Database.BeginTransactionAsync(ct);

            // The in-memory provider the unit tests use has no locks.
            if (attempt.Database.IsRelational()) await LockNamesAsync(attempt, ordered, ct);

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
    /// </summary>
    private static Task LockNameAsync(ResourceDbContext dbContext, string resourceContainerName, string resourceName,
        CancellationToken cancellationToken) =>
        LockNamesAsync(dbContext, [new ResourceLocation(resourceContainerName, resourceName)], cancellationToken);

    /// <summary>Locks each name for the transaction, in the order given, in one round trip.</summary>
    private static async Task LockNamesAsync(ResourceDbContext dbContext, IReadOnlyList<ResourceLocation> names,
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
        await dbContext.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, cancellationToken);
    }

    /// <summary>The application lock resource for a name: lock resources are limited to 255 characters, so it's hashed.</summary>
    internal static string NameLockResource(ResourceLocation name) =>
        "Resourcefull:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{name.ContainerName}\n{name.ResourceName}")));

    private static async Task ThrowIfNotMetAsync(ResourceDbContext dbContext, ResourceConditions conditions,
        string resourceContainerName, string resourceName, CancellationToken cancellationToken)
    {
        if (conditions.IfMatchVersionId is null && conditions.IfMatchContentHash is null) return;

        var current = await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.IsLatest)
            .Select(r => new { r.VersionId, r.ContentHash })
            .FirstOrDefaultAsync(cancellationToken);
        ThrowIfNotMet(conditions, resourceContainerName, resourceName, current?.VersionId, current?.ContentHash);
    }

    /// <summary>
    /// A <see langword="null"/> <paramref name="currentVersionId"/> means the name has no current version, which meets
    /// no condition; empty conditions are no conditions.
    /// </summary>
    private static void ThrowIfNotMet(ResourceConditions conditions, string resourceContainerName, string resourceName,
        string? currentVersionId, string? currentContentHash)
    {
        if (conditions.IfMatchVersionId is null && conditions.IfMatchContentHash is null) return;

        if (currentVersionId is null
            || (conditions.IfMatchVersionId is { } id && id != currentVersionId)
            || (conditions.IfMatchContentHash is { } hash && hash != currentContentHash))
        {
            throw new ResourcePreconditionFailedException(currentVersionId is null
                ? $"The resource {resourceName} in the container {resourceContainerName} has no current version to " +
                    "meet the conditions."
                : $"The current version {currentVersionId} of the resource {resourceName} in the container " +
                    $"{resourceContainerName} doesn't meet the conditions.",
                currentVersionId, currentContentHash)
            {
                ContainerName = resourceContainerName, ResourceName = resourceName
            };
        }
    }

    private static ResourceConflictException ResourceExists(string resourceContainerName, string resourceName) =>
        new($"The resource {resourceName} already exists in the container {resourceContainerName}.")
        {
            ContainerName = resourceContainerName, ResourceName = resourceName
        };

    private static ResourceConflictException VersionExists(string resourceContainerName, string resourceName,
        string versionId, Exception? innerException = null)
    {
        string message =
            $"The resource {resourceName} in the container {resourceContainerName} already has a version {versionId}.";
        return innerException is null
            ? new(message) { ContainerName = resourceContainerName, ResourceName = resourceName }
            : new(message, innerException) { ContainerName = resourceContainerName, ResourceName = resourceName };
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation };

    public async Task<List<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.IsLatest)
            .Select(AsResourceInfo)
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
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ResourceInfo>> GetResourceVersionsAsync(string resourceContainerName, string resourceName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
            .OrderByDescending(r => r.IsLatest)
            .ThenByDescending(r => r.Id)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ResourceInfo>> GetVersionPageAsync(string resourceContainerName, string? afterName,
        int maxNames, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // The page's names, found as GetResourcePageAsync finds them, and every version of each, in one query.
        IQueryable<ResourceModel> current =
            dbContext.Resources.Where(r => r.ContainerName == resourceContainerName && r.IsLatest);
        if (afterName is not null) current = current.Where(r => string.Compare(r.Name, afterName) > 0);
        IQueryable<string> names = current.OrderBy(r => r.Name).Take(maxNames).Select(r => r.Name);

        return await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && names.Contains(r.Name))
            .OrderBy(r => r.Name)
            .ThenByDescending(r => r.IsLatest)
            .ThenByDescending(r => r.Id)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken);
    }

    public async Task<ResourceInfo?> GetResourceInfoAsync(string resourceContainerName, string resourceName,
        string? versionId = null, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        ResourceModel? existingResource = await ResolveAsync(dbContext.Resources, resourceContainerName, resourceName, versionId, cancellationToken);

        if (existingResource is null) return null;

        return ToResourceInfo(existingResource);
    }

    /// <summary>A stored version as the clients see it, for queries to project to.</summary>
    private static readonly Expression<Func<ResourceModel, ResourceInfo>> AsResourceInfo = r => new ResourceInfo
    {
        ContainerName = r.ContainerName,
        Name = r.Name,
        VersionId = r.VersionId!, // A required column, generated on insert.
        LastModified = r.LastModified,
        IsLatest = r.IsLatest,
        ContentType = r.ContentType,
        ContentLength = r.ContentLength,
        ContentHash = r.ContentHash,
        Metadata = r.Metadata,
        Uri = r.Uri,
        CopiedFrom = r.CopiedFromVersionId == null ? null
            : new ResourceOrigin(r.CopiedFromContainerName!, r.CopiedFromName!, r.CopiedFromVersionId),
        ExpiresAt = r.ExpiresAt
    };

    /// <summary>A stored version already read, as the clients see it.</summary>
    private static readonly Func<ResourceModel, ResourceInfo> ToResourceInfo = AsResourceInfo.Compile();

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
                    await using DbCommand command = NewCommand(dbContext);
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
        if (versionId == null) return DeleteNameAsync(resourceContainerName, resourceName, conditions, cancellationToken);

        bool deletedInAnEarlierAttempt = false;

        // Under the name's lock, so a concurrent delete of the same version finds it gone instead of failing, the
        // promotion of the next version can't race a write, and the conditions hold until the delete commits.
        return WriteNameAsync(resourceContainerName, resourceName, async (dbContext, ct) =>
        {
            // A retry after a commit whose acknowledgement was lost finds what it deleted gone. The conditions it met
            // no longer hold then, and it mustn't report that nothing was deleted.
            if (deletedInAnEarlierAttempt && !await dbContext.Resources.AnyAsync(r => r.ContainerName == resourceContainerName
                && r.Name == resourceName && r.VersionId == versionId, ct))
                return true;

            await ThrowIfNotMetAsync(dbContext, conditions, resourceContainerName, resourceName, ct);
            deletedInAnEarlierAttempt = await ResolveWithChunksAsync(dbContext.Resources, resourceContainerName, resourceName, versionId, ct) is { } found
                && await DeleteVersionAsync(dbContext, found.Version, found.Chunks, ct);
            return deletedInAnEarlierAttempt;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes every version of a name, in batches that each commit on their own, so no transaction holds a name's whole
    /// history. The versions are the ones the name had when the conditions were checked, oldest first and the current one
    /// last, so the name keeps its current version until the end. A version stored meanwhile is newer, and stays, as if
    /// it had been stored just after the delete. A delete that's canceled or fails partway leaves every version it
    /// didn't reach whole, and calling it again finishes the job.
    /// </summary>
    private async Task<bool> DeleteNameAsync(string resourceContainerName, string resourceName, ResourceConditions conditions,
        CancellationToken cancellationToken)
    {
        List<(long Id, int Chunks)> versions = [];
        int deleted = 0;
        bool deletedAny = false;
        do
        {
            // Under the name's lock, so a batch can't race a write that makes a version current.
            deleted += await WriteNameAsync(resourceContainerName, resourceName, async (dbContext, ct) =>
            {
                // A retry after a commit whose acknowledgement was lost keeps the list it took, and doesn't check the
                // conditions again: with versions deleted, they may no longer hold.
                bool firstBatch = versions.Count == 0;
                if (firstBatch)
                {
                    await ThrowIfNotMetAsync(dbContext, conditions, resourceContainerName, resourceName, ct);
                    versions = [.. (await dbContext.Resources
                        .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName)
                        .OrderBy(r => r.IsLatest).ThenBy(r => r.Id)
                        .Select(r => new { r.Id, Chunks = r.Chunks!.Count })
                        .ToListAsync(ct)).Select(v => (v.Id, v.Chunks))];
                }

                List<(long Id, int Chunks)> batch = [];
                long chunks = 0;
                foreach ((long id, int count) in versions.Skip(deleted))
                {
                    if (batch.Count > 0 && chunks + count > ChunksPerDeleteBatch) break;
                    batch.Add((id, count));
                    chunks += count;
                }

                // The first batch's versions were read just now, under the name's lock, so they're removed by key. A
                // later batch loads its versions again, only those still under this name: a move between batches takes
                // them, IDs and all, to another.
                List<long> ids = [.. batch.Select(v => v.Id)];
                List<ResourceModel> found;
                if (firstBatch)
                {
                    found = [.. ids.Select(id => new ResourceModel { Id = id, ContainerName = resourceContainerName, Name = resourceName })];
                    dbContext.Resources.AttachRange(found);
                }
                else
                {
                    found = await dbContext.Resources
                        .Where(r => ids.Contains(r.Id) && r.ContainerName == resourceContainerName && r.Name == resourceName)
                        .ToListAsync(ct);
                }
                await DeleteLargeVersionsChunksAsync(dbContext, found.Select(r => (r.Id, batch.First(v => v.Id == r.Id).Chunks)), ct);
                dbContext.Resources.RemoveRange(found);
                deletedAny |= found.Count > 0;
                await dbContext.SaveChangesAsync(ct);
                return batch.Count;
            }, cancellationToken);
        }
        while (deleted < versions.Count);

        return deletedAny;
    }

    /// <summary>
    /// Deletes, <see cref="ChunksPerDeleteStatement"/> at a time, the chunks of versions with more than that many, before
    /// the versions go. Smaller versions' chunks go with them, through the cascade, without a round trip apiece.
    /// </summary>
    private async Task DeleteLargeVersionsChunksAsync(ResourceDbContext dbContext, IEnumerable<(long Id, int Chunks)> versions,
        CancellationToken cancellationToken)
    {
        foreach ((long id, int chunks) in versions)
        {
            if (chunks > ChunksPerDeleteStatement) await DeleteChunksAsync(dbContext, id, cancellationToken);
        }
    }

    /// <summary>
    /// Deletes a version's chunks a statement at a time, in the caller's transaction, before the version itself goes:
    /// deleting thousands of chunks in one statement, as the cascade would, locks the whole chunk table.
    /// </summary>
    private async Task DeleteChunksAsync(ResourceDbContext dbContext, long resourceId, CancellationToken cancellationToken)
    {
        // The in-memory provider the unit tests use leaves them to the cascade.
        if (!dbContext.Database.IsRelational()) return;

        while (await dbContext.Database.ExecuteSqlAsync(
            $"DELETE TOP ({ChunksPerDeleteStatement}) FROM [ResourceChunks] WHERE [ResourceId] = {resourceId}", cancellationToken) == ChunksPerDeleteStatement)
        {
        }
    }

    /// <summary>
    /// Deletes one version, under its name's lock; when it's the current version, the most recently stored of the rest
    /// becomes current.
    /// </summary>
    private async Task<bool> DeleteVersionAsync(ResourceDbContext dbContext, ResourceModel version, int chunks, CancellationToken cancellationToken)
    {
        await DeleteLargeVersionsChunksAsync(dbContext, [(version.Id, chunks)], cancellationToken);
        dbContext.Resources.Remove(version);

        if (version.IsLatest)
        {
            ResourceModel? next = await dbContext.Resources
                .Where(r => r.ContainerName == version.ContainerName && r.Name == version.Name && r.Id != version.Id)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (next != null) next.IsLatest = true;
        }

        return await dbContext.SaveChangesAsync(cancellationToken) > 0;
    }

    public async Task<List<ResourceInfo>> GetExpiredAsync(DateTimeOffset now, int maxVersions, ResourceInfo? after = null,
        CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // A seek on the index of versions that expire, so a sweep with nothing to delete reads only that.
        IQueryable<ResourceModel> expired = dbContext.Resources.Where(r => r.ExpiresAt != null && r.ExpiresAt <= now);
        if (after is { ExpiresAt: { } afterExpiry, VersionId: { } afterVersion })
        {
            // Written as a range on the index's first column, so it seeks to where the last batch ended.
            expired = expired.Where(r => r.ExpiresAt >= afterExpiry
                && (r.ExpiresAt > afterExpiry || string.Compare(r.VersionId, afterVersion) > 0));
        }

        return await expired
            .OrderBy(r => r.ExpiresAt)
            .ThenBy(r => r.VersionId)
            .Take(maxVersions)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> DeleteExpiredVersionAsync(ResourceLocation resource, string versionId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        bool deletedInAnEarlierAttempt = false;

        // Under the name's lock, so a change to the expiry can't come between the check and the delete.
        return WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
        {
            // A retry after a commit whose acknowledgement was lost finds the version gone.
            if (await ResolveWithChunksAsync(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId, ct) is not { } found)
                return deletedInAnEarlierAttempt;
            if (!(found.Version.ExpiresAt <= now)) return false;
            return deletedInAnEarlierAttempt = await DeleteVersionAsync(dbContext, found.Version, found.Chunks, ct);
        }, cancellationToken);
    }

    public async Task<IReadOnlyCollection<(ResourceLocation Resource, string VersionId)>> DeleteExpiredVersionsAsync(
        IReadOnlyList<(ResourceLocation Resource, string VersionId)> versions, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var deleted = new List<(ResourceLocation, string)>();
        // The expired versions left for a later batch, with their chunks.
        List<(ResourceLocation Resource, string VersionId, int Chunks)> later = [];
        List<(ResourceLocation Resource, string VersionId)> batch = [.. versions.Distinct()];
        while (batch.Count > 0)
        {
            // What an earlier attempt deleted, should a retry follow a commit whose acknowledgement was lost.
            List<(ResourceLocation, string)> deletedInAnEarlierAttempt = [];

            // A transaction under the batch's names' locks, so a change to an expiry can't come between the check and
            // the delete. It deletes up to ChunksPerDeleteBatch chunks' worth, as a container or name delete does, and
            // leaves the rest of the expired versions for the next.
            List<(ResourceLocation Resource, string VersionId)> current = batch;
            (List<(ResourceLocation, string)> gone, List<(ResourceLocation, string, int)> left) = await WriteNamesAsync(
                current.Select(v => v.Resource),
                async (dbContext, ct) =>
                {
                    var result = await DeleteExpiredBatchAsync(dbContext, current, now, deletedInAnEarlierAttempt, ct);
                    deletedInAnEarlierAttempt = result.Deleted;
                    return result;
                }, cancellationToken);
            deleted.AddRange(gone);

            // The first batch is every version, as it doesn't know their chunks yet; each later one is only those that
            // fit the budget, so it locks only the names it deletes.
            later = [.. left, .. later];
            int count = CountWithinDeleteBudget(later.Select(v => v.Chunks));
            batch = [.. later.Take(count).Select(v => (v.Resource, v.VersionId))];
            later = [.. later.Skip(count)];
        }
        return deleted;
    }

    /// <summary>
    /// How many of a list of versions, with these chunks each, go in one batch: as many as fit in
    /// <see cref="ChunksPerDeleteBatch"/>, and at least one.
    /// </summary>
    private int CountWithinDeleteBudget(IEnumerable<int> chunks)
    {
        int count = 0;
        long total = 0;
        foreach (int versionChunks in chunks)
        {
            if (count > 0 && total + versionChunks > ChunksPerDeleteBatch) break;
            count++;
            total += versionChunks;
        }
        return count;
    }

    /// <summary>
    /// Deletes those of <paramref name="versions"/> still expired, up to <see cref="ChunksPerDeleteBatch"/> chunks' worth,
    /// and returns the versions it deleted and those still expired, with their chunks, that it left for another batch.
    /// </summary>
    private async Task<(List<(ResourceLocation, string)> Deleted, List<(ResourceLocation, string, int)> Left)>
        DeleteExpiredBatchAsync(ResourceDbContext dbContext, List<(ResourceLocation Resource, string VersionId)> versions,
            DateTimeOffset now, List<(ResourceLocation, string)> deletedInAnEarlierAttempt, CancellationToken ct)
    {
        string[] ids = [.. versions.Select(v => v.VersionId).Distinct()];
        string[] batchContainers = [.. versions.Select(v => v.Resource.ContainerName).Distinct()];
        string[] batchNames = [.. versions.Select(v => v.Resource.ResourceName).Distinct()];
        var wanted = versions.ToHashSet();
        // By container and name first, so it seeks on the clustered index rather than scanning the table.
        var found = (await dbContext.Resources
                .Where(r => batchContainers.Contains(r.ContainerName) && batchNames.Contains(r.Name)
                    && ids.Contains(r.VersionId))
                .Select(r => new { Version = r, Chunks = r.Chunks!.Count })
                .ToListAsync(ct))
            .Where(f => wanted.Contains(
                (new ResourceLocation(f.Version.ContainerName, f.Version.Name), f.Version.VersionId!)))
            .ToList();
        var expired = found.Where(f => f.Version.ExpiresAt <= now).ToList();
        var deleting = expired.Take(CountWithinDeleteBudget(expired.Select(e => e.Chunks))).ToList();

        await DeleteLargeVersionsChunksAsync(dbContext, deleting.Select(d => (d.Version.Id, d.Chunks)), ct);
        dbContext.Resources.RemoveRange(deleting.Select(d => d.Version));

        // A name whose current version goes gets its most recently stored remaining version as current, as
        // DeleteVersionAsync does for one.
        var lostCurrent = deleting.Where(d => d.Version.IsLatest)
            .Select(d => (d.Version.ContainerName, d.Version.Name)).ToHashSet();
        if (lostCurrent.Count > 0)
        {
            long[] removed = [.. deleting.Select(d => d.Version.Id)];
            string[] containers = [.. lostCurrent.Select(n => n.ContainerName).Distinct()];
            string[] names = [.. lostCurrent.Select(n => n.Name).Distinct()];
            List<ResourceModel> next = await dbContext.Resources
                .Where(r => containers.Contains(r.ContainerName) && names.Contains(r.Name)
                    && !removed.Contains(r.Id)
                    && r.Id == dbContext.Resources
                        .Where(o => o.ContainerName == r.ContainerName && o.Name == r.Name
                            && !removed.Contains(o.Id))
                        .Max(o => o.Id))
                .ToListAsync(ct);
            foreach (ResourceModel version in next.Where(v => lostCurrent.Contains((v.ContainerName, v.Name))))
                version.IsLatest = true;
        }

        await dbContext.SaveChangesAsync(ct);

        // A version gone already counts if an earlier attempt deleted it.
        static (ResourceLocation, string) Key(ResourceModel version) =>
            (new ResourceLocation(version.ContainerName, version.Name), version.VersionId!);
        var foundKeys = found.Select(f => Key(f.Version)).ToHashSet();
        return ([.. deleting.Select(d => Key(d.Version)), .. deletedInAnEarlierAttempt.Where(v => !foundKeys.Contains(v))],
            [.. expired.Skip(deleting.Count).Select(e => (new ResourceLocation(e.Version.ContainerName, e.Version.Name),
                e.Version.VersionId!, e.Chunks))]);
    }

    public Task<bool> SetExpiryAsync(ResourceLocation resource, string versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default) =>
        WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
            await Version(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, expiresAt), ct) > 0, cancellationToken);

    public async Task AddResourceContainerAsync(ResourceContainerInfo resourceContainerInfo, CancellationToken cancellationToken = default)
    {
        bool created = false;
        await WriteNamesAsync([], async (attempt, ct) =>
        {
            attempt.ResourceContainers.Add(new ResourceContainerModel
            {
                Name = resourceContainerInfo.Name,
                Uri = resourceContainerInfo.Uri,
                Metadata = resourceContainerInfo.Metadata
            });

            try
            {
                await attempt.SaveChangesAsync(ct);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // A retry after a commit whose acknowledgement was lost finds the container it created. It can't tell
                // that from a commit that failed while another caller created the name, which counts as created too.
                if (created) return true;
                throw new ResourceConflictException($"The container {resourceContainerInfo.Name} already exists.",
                    exception) { ContainerName = resourceContainerInfo.Name };
            }

            return created = true;
        }, cancellationToken);
    }

    public async Task<bool> SetResourceContainerMetadataAsync(string resourceContainerName, IDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        IDictionary<string, string> copy = new Dictionary<string, string>(metadata);
        return await dbContext.ResourceContainers.Where(x => x.Name == resourceContainerName)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Metadata, copy), cancellationToken) > 0;
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

        // One aggregate over the container's version rows, from their recorded lengths, which doesn't touch the chunks.
        // Only if an early release stored versions without a length are their chunks' lengths added up, separately.
        UsageRow usage = (await dbContext.Database.SqlQuery<UsageRow>($"""
            SELECT COALESCE(SUM(r.[ContentLength]), 0) AS [StoredSize],
                COUNT_BIG(CASE WHEN r.[IsLatest] = 1 THEN 1 END) AS [ResourceCount],
                COUNT_BIG(*) AS [VersionCount],
                COUNT_BIG(CASE WHEN r.[ContentLength] IS NULL THEN 1 END) AS [Unmeasured]
            FROM [Resources] r
            WHERE r.[ContainerName] = {resourceContainerName} AND r.[StagedAt] IS NULL
            """).ToListAsync(cancellationToken))[0];
        long storedSize = usage.StoredSize;
        if (usage.Unmeasured > 0)
        {
            storedSize += await dbContext.Database.SqlQuery<long>($"""
                SELECT COALESCE(SUM(CAST(DATALENGTH(ch.[Data]) AS bigint)), 0) AS [Value]
                FROM [Resources] r JOIN [ResourceChunks] ch ON ch.[ResourceId] = r.[Id]
                WHERE r.[ContainerName] = {resourceContainerName} AND r.[ContentLength] IS NULL AND r.[StagedAt] IS NULL
                """).SingleAsync(cancellationToken);
        }

        return new ResourceContainerUsage(storedSize, usage.ResourceCount, usage.VersionCount);
    }

    public async Task<bool> DeleteResourceContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        if (!await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken)) return false;

        // Each batch commits in a transaction of its own, so no single transaction holds the whole container's content.
        // A batch takes whole resources, each with its chunks, so a reader never sees part of one, until it reaches the
        // chunk budget. Each pass removes only what earlier passes left, so a delete that is canceled or fails partway
        // is finished by calling it again.
        while (await WriteNamesAsync([], DeleteBatchAsync, cancellationToken) > 0)
        {
        }

        async Task<int> DeleteBatchAsync(ResourceDbContext attempt, CancellationToken ct)
        {
            // Staged versions too, so an abandoned upload's chunks go in batches with the rest rather than all at once
            // when the container's row does.
            var candidates = await attempt.Resources.IgnoreQueryFilters()
                .Where(r => r.ContainerName == resourceContainerName)
                .OrderBy(r => r.Name).ThenBy(r => r.VersionId)
                .Select(r => new { r.Id, r.Name, r.IsLatest, Chunks = r.Chunks!.Count })
                .Take(MaxResourcesPerDeleteBatch)
                .ToListAsync(ct);
            if (candidates.Count == 0) return 0;

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

            // A large version's chunks are deleted in statements of their own before its row. Its row is locked first,
            // and checked to be still in this container, so a move can't take it elsewhere in between and leave it
            // without its content; a smaller version's row and chunks go in one statement that checks the container.
            var deleting = new List<(long Id, int Chunks)>();
            foreach (var candidate in candidates.Where(c => batch.Contains(c.Id)))
            {
                if (candidate.Chunks > ChunksPerDeleteStatement && attempt.Database.IsRelational()
                    && (await attempt.Database.SqlQuery<int>($"""
                        SELECT 1 AS [Value] FROM [Resources] WITH (UPDLOCK, ROWLOCK)
                        WHERE [Id] = {candidate.Id} AND [ContainerName] = {resourceContainerName}
                        """).ToListAsync(ct)).Count == 0)
                    continue;
                deleting.Add((candidate.Id, candidate.Chunks));
            }

            await DeleteLargeVersionsChunksAsync(attempt, deleting, ct);
            if (attempt.Database.IsRelational())
            {
                // A statement per version, in one round trip, rather than loading and tracking each to delete it: SQL
                // Server removes each version's chunks through the cascade with a seek. One statement for the whole
                // batch, or one that also matches the container, cascades with a scan of the chunk table instead,
                // which locks all of it. So the versions still in the container are locked and listed first, by ID, and
                // only those are deleted: a version a move took to another container since it was listed stays there.
                var ids = string.Join(", ", batch.Select((_, i) => $"@id{i}"));
                var sql = new StringBuilder("DECLARE @here TABLE ([Id] bigint PRIMARY KEY); " +
                    "INSERT INTO @here SELECT [Id] FROM [Resources] WITH (UPDLOCK, ROWLOCK, INDEX([PK_Resources])) " +
                    $"WHERE [Id] IN ({ids}) AND [ContainerName] = @container;");
                var parameters = new object[batch.Count + 1];
                parameters[0] = new SqlParameter("@container", resourceContainerName);
                for (int i = 0; i < batch.Count; i++)
                {
                    sql.Append($"IF EXISTS (SELECT 1 FROM @here WHERE [Id] = @id{i}) ")
                        .Append($"DELETE FROM [Resources] WHERE [Id] = @id{i};");
                    parameters[i + 1] = new SqlParameter($"@id{i}", batch[i]);
                }
                await attempt.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, ct);
            }
            else
            {
                // The in-memory provider the unit tests use runs no SQL.
                attempt.Resources.RemoveRange(
                    await attempt.Resources.IgnoreQueryFilters().Where(r => batch.Contains(r.Id)).ToListAsync(ct));
                await attempt.SaveChangesAsync(ct);
            }
            return batch.Count;
        }

        bool deleted = false;
        return await WriteNamesAsync([], async (attempt, ct) =>
        {
            ResourceContainerModel? container = await attempt.ResourceContainers.FirstOrDefaultAsync(x => x.Name == resourceContainerName, ct);
            // A retry after a commit whose acknowledgement was lost finds the container gone.
            if (container is null) return deleted;

            attempt.ResourceContainers.Remove(container);
            await attempt.SaveChangesAsync(ct);
            return deleted = true;
        }, cancellationToken);
    }

    /// <summary>
    /// A command on the context's connection, with the command timeout its options give: a command made directly keeps
    /// SqlClient's default of 30 seconds.
    /// </summary>
    internal static DbCommand NewCommand(ResourceDbContext dbContext)
    {
        DbCommand command = dbContext.Database.GetDbConnection().CreateCommand();
        if (dbContext.Database.GetCommandTimeout() is { } timeout) command.CommandTimeout = timeout;
        return command;
    }

    private static Task<ResourceModel?> ResolveAsync(IQueryable<ResourceModel> query,
        string resourceContainerName, string resourceName, string? versionId, CancellationToken cancellationToken) =>
        Version(query, resourceContainerName, resourceName, versionId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>As <see cref="ResolveAsync"/>, with how many chunks the version has, for deleting it.</summary>
    private static async Task<(ResourceModel Version, int Chunks)?> ResolveWithChunksAsync(IQueryable<ResourceModel> query,
        string resourceContainerName, string resourceName, string? versionId, CancellationToken cancellationToken) =>
        await Version(query, resourceContainerName, resourceName, versionId)
            .Select(r => new { Version = r, Chunks = r.Chunks!.Count })
            .FirstOrDefaultAsync(cancellationToken) is { } found ? (found.Version, found.Chunks) : null;

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
        public long Unmeasured { get; set; }
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
