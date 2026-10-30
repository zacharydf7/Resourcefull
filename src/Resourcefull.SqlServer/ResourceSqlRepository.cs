using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.Utilities;
using System.Data;
using System.Data.Common;
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
    /// Caps the content one batch of a container or name delete removes, counted in chunks, so no single transaction has
    /// to log a whole container or a name's whole history. A resource bigger than the cap still goes in a batch of its own.
    /// </summary>
    internal int ChunksPerDeleteBatch { get; init; } = 256;

    /// <summary>
    /// How many chunks one statement deletes. SQL Server locks the whole chunk table, holding up every upload, once a
    /// statement holds 5,000 locks on the chunks' rows: one per chunk, since the locks on their content pages don't count
    /// toward it. A version with more chunks than this has them deleted this many at a time before it goes; a smaller one
    /// takes them with it.
    /// </summary>
    internal const int ChunksPerDeleteStatement = 1_000;

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
    private async Task<WrittenContent> AddVersionAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream,
        ResourceWriteCondition condition, DateTimeOffset? lastModified, CancellationToken cancellationToken)
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
            ResourceModel resource = NewVersion(resourceContainerName, resourceInfo);
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

            List<ResourceModel> priorLatest =
                await CurrentForWriteAsync(attempt, resourceContainerName, resourceInfo.Name, condition, ct);

            // Stamped under the name's lock as the version becomes current, not before its content was read. A wrong
            // declared length throws here, before anything about the name changes.
            written = content.Complete(resourceInfo.ContentLength, lastModified ?? _clock.GetUtcNow());

            attempt.Attach(resource);
            await MakeCurrentAsync(attempt, priorLatest, resource, written, ct);

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
            await ThrowIfContainerMissingAsync(resourceContainerName, exception, cancellationToken);
            throw;
        }
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
            throw new ResourceContainerNotFoundException($"Resource Container with the name {resourceContainerName} does not exist.", exception);
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
                    return written ?? throw new ResourceConflictException($"Resource {destination.Name} already has a version {destination.VersionId}.");
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
                if (sourceConditions is not null) ThrowIfNotMet(sourceConditions, source.ResourceName, current.VersionId, current.ContentHash);
                if (await attempt.Resources.AnyAsync(r => r.ContainerName == destination.ContainerName && r.Name == destination.ResourceName, ct))
                    throw new ResourceConflictException($"Resource with name {destination.ResourceName} already exists.");

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
            throw new ResourceConflictException($"Resource with name {resourceName} already exists.");
        if (condition.Conditions is { } conditions)
        {
            ResourceModel? current = priorLatest.FirstOrDefault();
            ThrowIfNotMet(conditions, resourceName, current?.VersionId, current?.ContentHash);
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
    /// Inserts a batch of chunks in one command, in the context's transaction, and empties the batch. The command is
    /// sent straight through SqlClient: tracking each chunk, or sending the command through EF Core's raw SQL, more than
    /// doubled what an upload allocated.
    /// </summary>
    private static async Task SaveChunksAsync(ResourceDbContext dbContext, List<ResourceChunkModel> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        if (dbContext.Database.IsRelational())
        {
            await using DbCommand command = NewCommand(dbContext);
            command.Transaction = dbContext.Database.CurrentTransaction!.GetDbTransaction();
            var sql = new StringBuilder("INSERT INTO [ResourceChunks] ([ResourceId], [Index], [Data]) VALUES ");
            command.Parameters.Add(new SqlParameter("@resourceId", batch[0].ResourceId));
            for (int i = 0; i < batch.Count; i++)
            {
                sql.Append(i == 0 ? "" : ", ").Append($"(@resourceId, @index{i}, @data{i})");
                command.Parameters.Add(new SqlParameter($"@index{i}", batch[i].Index));
                command.Parameters.Add(new SqlParameter($"@data{i}", SqlDbType.VarBinary, -1) { Value = batch[i].Data });
            }
            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            // The in-memory provider the unit tests use runs no SQL; untracked after, so it doesn't keep the content.
            dbContext.ResourceChunks.AddRange(batch);
            await dbContext.SaveChangesAsync(cancellationToken);
            foreach (ResourceChunkModel chunk in batch) dbContext.Entry(chunk).State = EntityState.Detached;
        }

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
            if (attempt.Database.IsRelational())
            {
                foreach (ResourceLocation name in ordered)
                    await LockNameAsync(attempt, name.ContainerName, name.ResourceName, ct);
            }

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
                throw new ResourceConflictException($"Resource Container with the name {resourceContainerInfo.Name} already exists.", exception);
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
            WHERE r.[ContainerName] = {resourceContainerName}
            """).ToListAsync(cancellationToken))[0];
        long storedSize = usage.StoredSize;
        if (usage.Unmeasured > 0)
        {
            storedSize += await dbContext.Database.SqlQuery<long>($"""
                SELECT COALESCE(SUM(CAST(DATALENGTH(ch.[Data]) AS bigint)), 0) AS [Value]
                FROM [Resources] r JOIN [ResourceChunks] ch ON ch.[ResourceId] = r.[Id]
                WHERE r.[ContainerName] = {resourceContainerName} AND r.[ContentLength] IS NULL
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
            var candidates = await attempt.Resources
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
                attempt.Resources.RemoveRange(await attempt.Resources.Where(r => batch.Contains(r.Id)).ToListAsync(ct));
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
