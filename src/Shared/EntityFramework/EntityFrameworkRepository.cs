using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Resourcefull.Providers;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.ExceptionServices;

namespace Resourcefull.EntityFramework;

/// <summary>
/// Keeps resources in a database through EF Core: each version a row, its content in chunks of
/// <see cref="ResourceChunker.ChunkSize"/>, and each name's writes in turn under a lock on the name. Each provider derives
/// its repository from this one, which it compiles from source, and supplies what differs between databases: the lock on
/// a name, the statements EF Core can't express, how its errors are recognized, how precisely it keeps times, and which
/// values it can't store.
/// </summary>
internal abstract class EntityFrameworkRepository(IDbContextFactory<ResourceDbContext> dbContextFactory, TimeProvider? timeProvider,
    string? storeName, IReadOnlySet<string>? searchableMetadataKeys) : ResourceRepository(searchableMetadataKeys)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The store's name, for telemetry; <see langword="null"/> for the store without one.</summary>
    protected string? StoreName => storeName;

    private const int MaxResourcesPerDeleteBatch = 100;

    // At least 1 MiB of content per round trip while uploading and downloading.
    internal const int ChunksPerUploadBatch = (1024 * 1024 + ResourceChunker.ChunkSize - 1) / ResourceChunker.ChunkSize;
    protected const int ChunksPerReadPage = ChunksPerUploadBatch;

    /// <summary>
    /// Caps the content one batch of a container or name delete, or of an expiry sweep, removes, counted in chunks, so no
    /// single transaction has to log a whole container or a name's whole history. A resource bigger than the cap still
    /// goes in a batch of its own.
    /// </summary>
    internal int ChunksPerDeleteBatch { get; init; } = 256;

    /// <summary>
    /// How many chunks one statement deletes: each database's limit keeps a statement from locking too much, or running
    /// too long, as the cascade could for a large version. A version with more chunks than this has them deleted this
    /// many at a time before it goes; a smaller one takes them with it, through the cascade.
    /// </summary>
    protected abstract int ChunksDeletedPerStatement { get; }

    /// <summary>
    /// The time a version is stamped with: the clock's, cut to the database's precision when it keeps times less
    /// precisely, so a version's times read back as the write returned them.
    /// </summary>
    protected virtual DateTimeOffset Now() => _clock.GetUtcNow();

    /// <summary>
    /// The database's name when it can't store U+0000 in text, for the message that refuses a value with one; otherwise
    /// <see langword="null"/>. If it can't, no stored name, version ID or content type contains one, so a lookup of a
    /// value with one finds nothing without querying the database, and a write with one is refused before anything is
    /// stored.
    /// </summary>
    protected virtual string? DatabaseRefusingNul => null;

    /// <summary>
    /// Whether the database may hold versions an early release stored without a length or a hash: their length is then
    /// added up from their chunks, and a copy reads and hashes their content.
    /// </summary>
    protected virtual bool MayHoldUnmeasuredVersions => false;

    /// <summary>Whether a failure, or the one it wraps, is the database refusing a row whose container doesn't exist.</summary>
    protected abstract bool IsForeignKeyViolation(Exception exception);

    /// <summary>Whether a failure, or the one it wraps, is the database refusing a row a unique index already has.</summary>
    protected abstract bool IsUniqueViolation(Exception exception);

    /// <summary>
    /// The versions whose names start with <paramref name="prefix"/>, matched ordinally and case-sensitively, as EF
    /// Core's <c>LIKE</c> matches them with SQL Server's and PostgreSQL's collations. A provider whose database's
    /// <c>LIKE</c> ignores case overrides this to narrow the match. The caller also bounds the query by
    /// <paramref name="afterName"/>, the name a listing continues after, if any.
    /// </summary>
    protected virtual IQueryable<ResourceModel> WithNamePrefix(IQueryable<ResourceModel> query, string prefix, string? afterName) =>
        query.Where(r => r.Name.StartsWith(prefix));

    /// <summary>The containers whose names start with <paramref name="prefix"/>, as <see cref="WithNamePrefix"/> matches names.</summary>
    protected virtual IQueryable<ResourceContainerModel> WithContainerPrefix(IQueryable<ResourceContainerModel> query, string prefix,
        string? afterName) =>
        query.Where(c => c.Name.StartsWith(prefix));

    /// <summary>Counts the abandoned uploads' versions a sweep deleted, in the provider's own telemetry.</summary>
    protected abstract void CountAbandonedUploads(int deleted);

    /// <summary>
    /// How often an upload moves its staged version's <see cref="ResourceModel.StagedAt"/> forward, as a chunk arrives:
    /// well within the 24 hours after which the expiry sweep takes a staged version as abandoned, so an upload is safe
    /// as long as its content keeps arriving.
    /// </summary>
    private static readonly TimeSpan StagedStampInterval = TimeSpan.FromMinutes(10);

    /// <summary>A staged version's hash until it's stored: as long as a real one, which replaces it.</summary>
    private static readonly string StagedContentHash = "sha256:" + new string('0', 64);

    public override Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
        ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) =>
        AddVersionAsync(version, content, condition ?? new ResourceWriteCondition(), null, cancellationToken);

    public override Task<WrittenContent> ImportVersionAsync(NewResourceVersion version, Stream content,
        CancellationToken cancellationToken = default) =>
        AddVersionAsync(version, content, new ResourceWriteCondition(), version.LastModified, cancellationToken);

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
    private async Task<WrittenContent> AddVersionAsync(NewResourceVersion version, Stream stream,
        ResourceWriteCondition condition, DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        ThrowIfUnstorable(version);
        (string resourceContainerName, string resourceName) = version.Resource;
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        ResourceModel? staged = null;
        bool stored = false;
        try
        {
            staged = await StartVersionAsync(strategy, version, condition, cancellationToken).ConfigureAwait(false);
            using var content = new HashingStream(stream);
            int batches = await StoreContentAsync(dbContext, strategy, staged.Id, content, cancellationToken).ConfigureAwait(false);
            // On the client's span only, never an application's own; added to, so an import's counts every version's.
            if (Activity.Current is { } activity && activity.Source.Name == ResourcefullTelemetry.ActivitySourceName)
            {
                int before = activity.GetTagItem("resourcefull.batches") as int? ?? 0;
                activity.SetTag("resourcefull.batches", before + batches);
            }
            WrittenContent written = await FinishVersionAsync(version, staged, content, condition, lastModified,
                cancellationToken).ConfigureAwait(false);
            stored = true;
            return written;
        }
        catch (Exception exception) when (IsForeignKeyViolation(exception) || exception is DbUpdateConcurrencyException)
        {
            // Its container was deleted meanwhile, taking the staged version with it; or the staged version alone is
            // gone, as the sweep of abandoned uploads takes one whose upload stored nothing for a day.
            await ThrowIfContainerMissingAsync(resourceContainerName, exception, cancellationToken).ConfigureAwait(false);
            if (staged is null) throw;
            throw new ResourceUploadInterruptedException($"The resource {resourceName} wasn't stored in the container " +
                $"{resourceContainerName}: its version was deleted before it was complete.", exception)
            {
                ContainerName = resourceContainerName, ResourceName = resourceName
            };
        }
        finally
        {
            if (!stored && staged is not null)
            {
                try
                {
                    await DeleteStagedVersionAsync(strategy, staged.Id, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The upload's own failure is the one to report; the sweep of abandoned uploads deletes this later.
                }
            }
        }
    }

    /// <summary>
    /// Inserts the version, staged, after the checks that spare reading the content of an upload that would fail, and
    /// returns it, untracked. Without checks the insert is a statement of its own, which commits as it runs; with them,
    /// the checks and the insert share a transaction.
    /// </summary>
    private async Task<ResourceModel> StartVersionAsync(IExecutionStrategy strategy, NewResourceVersion version,
        ResourceWriteCondition condition, CancellationToken cancellationToken)
    {
        (string resourceContainerName, string resourceName) = version.Resource;
        bool committing = false;
        return await strategy.ExecuteAsync(async ct =>
        {
            using ResourceDbContext attempt = NewWriteContext();

            // A retry after a commit whose acknowledgment was lost finds its own version staged. A first attempt skips
            // the look, a round trip on every write: the insert below is refused if the name already has the version.
            // The in-memory provider the SQL Server unit tests use doesn't enforce the unique index.
            if (committing || !attempt.Database.IsRelational())
            {
                ResourceModel? existing = await attempt.Resources.IgnoreQueryFilters().AsNoTracking()
                    .FirstOrDefaultAsync(r => r.ContainerName == resourceContainerName && r.Name == resourceName
                        && r.VersionId == version.VersionId, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    // Its own only while staged: a stored version with the ID, which an import names, is another's.
                    if (committing && existing.StagedAt is not null) return existing;
                    throw VersionExists(resourceContainerName, resourceName, version.VersionId);
                }
            }

            // Conditions are checked after the insert, so a version that fails them mustn't stay; create-only is
            // checked before it, so needs no transaction. Disposed in a finally rather than with await using, since
            // ConfigureAwait's wrapper can't dispose null.
            IDbContextTransaction? transaction = condition.Conditions is not null
                ? await attempt.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                : null;
            try
            {
                // Checked again as the version becomes current; this only spares reading the content of an upload that
                // will fail.
                if (condition.CreateOnly && await attempt.Resources.AnyAsync(r => r.ContainerName == resourceContainerName
                    && r.Name == resourceName && r.IsLatest, ct).ConfigureAwait(false))
                    throw ResourceExists(resourceContainerName, resourceName);

                // Inserted first, so a missing container is reported before any content is read.
                ResourceModel resource = NewVersion(version);
                resource.StagedAt = Now();
                // With a hash as long as its real one, so making it current changes the row in place. Inserted
                // without one, SQL Server's version index's pages were measured half full after storing 500 names, so a
                // page of a listing read 12 pages rather than 8. Nothing reads a staged version to see it.
                resource.ContentHash = StagedContentHash;
                attempt.Resources.Add(resource);
                // Without a transaction, the insert commits as it runs.
                committing = transaction is null;
                try
                {
                    await attempt.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateException exception) when (IsUniqueViolation(exception))
                {
                    throw VersionExists(resourceContainerName, resourceName, version.VersionId, exception);
                }

                // Checked again as the version becomes current, like create-only. After the insert, so a missing
                // container is reported rather than unmet conditions.
                if (condition.Conditions is { } conditions)
                    await ThrowIfNotMetAsync(attempt, conditions, resourceContainerName, resourceName, ct).ConfigureAwait(false);

                if (transaction is not null)
                {
                    committing = true;
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                }
                attempt.Entry(resource).State = EntityState.Detached;
                return resource;
            }
            finally
            {
                if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
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
        DateTimeOffset lastStamped = Now();
        var batch = new List<ResourceChunkModel>(ChunksPerUploadBatch);
        // Each batch is stored before the chunker reuses its buffers: it shares ChunksPerUploadBatch of them in turn,
        // and a batch holds at most that many chunks, however early it goes.
        await foreach (ResourceChunkModel chunk in
            ResourceChunker.ChunksAsync(content, ChunksPerUploadBatch, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            chunk.ResourceId = resourceId;
            batch.Add(chunk);
            bool stamp = Now() - lastStamped >= StagedStampInterval;
            if (batch.Count < ChunksPerUploadBatch && !stamp) continue;

            await StoreBatchAsync(dbContext, strategy, batch, stamp, cancellationToken).ConfigureAwait(false);
            batches++;
            if (stamp) lastStamped = Now();
        }
        if (batch.Count == 0) return batches;
        await StoreBatchAsync(dbContext, strategy, batch, stamp: false, cancellationToken).ConfigureAwait(false);
        return batches + 1;
    }

    /// <summary>
    /// Makes the staged version its name's current one, under the name's lock, once the conditions and a declared length
    /// hold, and returns what was stored.
    /// </summary>
    private Task<WrittenContent> FinishVersionAsync(NewResourceVersion version, ResourceModel staged, HashingStream content,
        ResourceWriteCondition condition, DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        (string resourceContainerName, string resourceName) = version.Resource;
        WrittenContent? written = null;
        return WriteNameAsync(resourceContainerName, resourceName, async (attempt, ct) =>
        {
            // A retry after a commit whose acknowledgment was lost finds it current, which the filter lets it see.
            if (written is not null && await attempt.Resources.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == staged.Id, ct).ConfigureAwait(false) is { } stored)
            {
                return new WrittenContent(stored.ContentLength!.Value, stored.ContentHash!, stored.LastModified);
            }

            List<ResourceModel> priorLatest =
                await CurrentForWriteAsync(attempt, resourceContainerName, resourceName, condition, ct).ConfigureAwait(false);

            // Stamped under the name's lock as the version becomes current, not before its content was read. A wrong
            // declared length throws here, before anything about the name changes.
            WrittenContent completed = content.Complete(version.DeclaredLength, lastModified ?? Now());

            // Attached as it was staged, whatever an earlier attempt changed, so only what changes now is saved. Should
            // it be gone, the save throws DbUpdateConcurrencyException, which the caller reports.
            ResourceModel current = attempt.Resources.Attach(new ResourceModel
            {
                Id = staged.Id, ContainerName = staged.ContainerName, Name = staged.Name, StagedAt = staged.StagedAt
            }).Entity;
            current.StagedAt = null;
            await MakeCurrentAsync(attempt, priorLatest, current, completed, ct).ConfigureAwait(false);
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
            var transaction = await attempt.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var transactionDisposal = transaction.ConfigureAwait(false);

            // Locked as it's read, so an upload can't make it current, or move its stamp forward, before it goes. A retry
            // after a commit whose acknowledgment was lost finds it gone.
            List<int> chunks = attempt.Database.IsRelational()
                ? await LockStagedVersionAsync(attempt, resourceId, staleBefore, ct).ConfigureAwait(false)
                : await attempt.Resources.IgnoreQueryFilters()
                    .Where(r => r.Id == resourceId && r.StagedAt != null
                        && (staleBefore == null || r.StagedAt < staleBefore))
                    .Select(r => r.Chunks!.Count)
                    .ToListAsync(ct).ConfigureAwait(false);
            if (chunks.Count == 0) return deletedInAnEarlierAttempt;

            await DeleteLargeVersionsChunksAsync(attempt, [(resourceId, chunks[0])], ct).ConfigureAwait(false);
            attempt.Resources.Remove(attempt.Resources.Attach(
                new ResourceModel { Id = resourceId, ContainerName = "", Name = "" }).Entity);
            await attempt.SaveChangesAsync(ct).ConfigureAwait(false);
            deletedInAnEarlierAttempt = true;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    // Upload sessions. A session's version is staged as an upload's is, with the length appended so far in its
    // ContentLength, which a staged upload's leaves null until it's current: that's what tells the two apart. Appends
    // take no name lock: each batch moves the length forward only from the length it expects, in the transaction that
    // stores the batch's chunks, so two appends from the same offset can't both store.

    public override async Task StartStagedVersionAsync(NewResourceVersion version, CancellationToken cancellationToken = default)
    {
        ThrowIfUnstorable(version);
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        bool sentBefore = false;
        try
        {
            await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
            {
                using ResourceDbContext attempt = NewWriteContext();
                // A retry after a commit whose acknowledgment was lost finds the session it started.
                if (sentBefore && await Session(attempt, version.Resource, version.VersionId).AnyAsync(ct).ConfigureAwait(false))
                    return;
                sentBefore = true;

                ResourceModel resource = NewVersion(version);
                resource.StagedAt = Now();
                resource.ContentHash = StagedContentHash;
                resource.ContentLength = 0;
                resource.DeclaredLength = version.DeclaredLength;
                attempt.Resources.Add(resource);
                try
                {
                    await attempt.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateException exception) when (IsUniqueViolation(exception))
                {
                    throw VersionExists(version.Resource.ContainerName, version.Resource.ResourceName, version.VersionId, exception);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsForeignKeyViolation(exception))
        {
            await ThrowIfContainerMissingAsync(version.Resource.ContainerName, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public override async Task<StagedVersion?> GetStagedVersionAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        ResourceModel? staged = await Session(dbContext, resource, versionId).AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return staged is null
            ? null
            : new StagedVersion(new NewResourceVersion(resource, versionId)
            {
                ContentType = staged.ContentType,
                Metadata = new Dictionary<string, string>(staged.Metadata),
                ExpiresAt = staged.ExpiresAt,
                RetainUntil = staged.RetainUntil,
                LegalHold = staged.LegalHold,
                DeclaredLength = staged.DeclaredLength
            }, staged.ContentLength!.Value);
    }

    public override async Task<long?> AppendStagedContentAsync(ResourceAddress resource, string versionId, long offset,
        Stream content, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        var session = await Session(dbContext, resource, versionId)
            .Select(r => new { r.Id, Length = r.ContentLength!.Value, LastIndex = r.Chunks!.Max(c => (int?)c.Index) ?? 0 })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        if (session.Length != offset) throw OffsetNotAtEnd(resource, versionId, offset, session.Length);

        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        long length = offset;
        var batch = new List<ResourceChunkModel>(ChunksPerUploadBatch);
        // Each batch is stored before the chunker reuses its buffers, as an upload's is.
        await foreach (ResourceChunkModel chunk in ResourceChunker.ChunksAsync(content, ChunksPerUploadBatch, session.LastIndex,
            cancellationToken).ConfigureAwait(false))
        {
            chunk.ResourceId = session.Id;
            batch.Add(chunk);
            if (batch.Count < ChunksPerUploadBatch) continue;
            if (await AppendBatchAsync(strategy, resource, versionId, batch, length, cancellationToken).ConfigureAwait(false) is not { } stored)
                return null;
            length = stored;
        }
        if (batch.Count == 0) return length;
        return await AppendBatchAsync(strategy, resource, versionId, batch, length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a batch of an append's chunks, with the session's length moved forward by their size and its stamp moved
    /// on, provided its length is still <paramref name="expected"/>; then empties the batch.
    /// </summary>
    /// <returns>The session's length after the batch, or <see langword="null"/> if the session is gone.</returns>
    private async Task<long?> AppendBatchAsync(IExecutionStrategy strategy, ResourceAddress resource, string versionId,
        List<ResourceChunkModel> batch, long expected, CancellationToken cancellationToken)
    {
        long resourceId = batch[0].ResourceId;
        long after = expected + batch.Sum(c => (long)c.Data.Length);
        bool sentBefore = false;
        long? stored = await strategy.ExecuteAsync(async ct =>
        {
            bool retry = sentBefore;
            sentBefore = true;
            using ResourceDbContext attempt = NewWriteContext();
            var transaction = await attempt.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var transactionDisposal = transaction.ConfigureAwait(false);
            int moved = await MoveSessionLengthAsync(attempt, resourceId, expected, after, Now(), ct).ConfigureAwait(false);
            if (moved == 0)
            {
                long? current = await SessionLengthAsync(attempt, resourceId, ct).ConfigureAwait(false);
                // A retry after a commit whose acknowledgment was lost finds its own batch stored.
                if (current is null || (retry && current == after)) return current;
                throw OffsetNotAtEnd(resource, versionId, expected, current.Value);
            }

            DbCommand command = NewCommand(attempt);
            await using var commandDisposal = command.ConfigureAwait(false);
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = ResourceDbContext.QualifyTables(InsertChunks(command, batch, null), attempt.Schema);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return (long?)after;
        }, cancellationToken).ConfigureAwait(false);
        batch.Clear();
        return stored;
    }

    public override async Task<Stream?> GetStagedStreamAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default)
    {
        // The stream reads the chunks through this context as the caller reads, and disposes it.
        ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        try
        {
            var session = await Session(dbContext, resource, versionId)
                .Select(r => new { r.Id, Length = r.ContentLength!.Value })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (session is not null) return new ResourceStream(PageReader(dbContext, session.Id), session.Length, dbContext);
            await dbContext.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await dbContext.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override Task<WrittenContent?> CompleteStagedVersionAsync(ResourceAddress resource, string versionId,
        long contentLength, string contentHash, ResourceWriteCondition? condition = null,
        CancellationToken cancellationToken = default)
    {
        (string resourceContainerName, string resourceName) = resource;
        if (HasNul(resourceContainerName) || HasNul(resourceName) || HasNul(versionId)) return Task.FromResult<WrittenContent?>(null);
        condition ??= new ResourceWriteCondition();
        WrittenContent? written = null;
        long completedId = 0;
        return WriteNameAsync(resourceContainerName, resourceName, async (attempt, ct) =>
        {
            // Locked as it's read, so an append can't store a batch the measured length and hash don't cover.
            SessionRow? staged = await LockSessionAsync(attempt, resource, versionId, ct).ConfigureAwait(false);
            // A retry after a commit whose acknowledgment was lost finds the session gone, made current, which the
            // filter lets it see; one whose earlier attempt rolled back, and that something else deleted, finds nothing.
            if (staged is null)
            {
                return written is not null && await attempt.Resources.AnyAsync(r => r.Id == completedId, ct).ConfigureAwait(false)
                    ? written
                    : null;
            }
            if (staged.ContentLength != contentLength) throw OffsetNotAtEnd(resource, versionId, contentLength, staged.ContentLength);

            List<ResourceModel> priorLatest =
                await CurrentForWriteAsync(attempt, resourceContainerName, resourceName, condition, ct).ConfigureAwait(false);
            var completed = new WrittenContent(contentLength, contentHash, Now());
            ResourceModel current = attempt.Resources.Attach(new ResourceModel
            {
                Id = staged.Id, ContainerName = resourceContainerName, Name = resourceName, StagedAt = staged.StagedAt
            }).Entity;
            current.StagedAt = null;
            await MakeCurrentAsync(attempt, priorLatest, current, completed, ct).ConfigureAwait(false);
            completedId = staged.Id;
            return written = completed;
        }, cancellationToken);
    }

    public override async Task<bool> DeleteStagedVersionAsync(ResourceAddress resource, string versionId,
        CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        long? resourceId = await Session(dbContext, resource, versionId).Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return resourceId is { } id && await DeleteStagedVersionAsync(dbContext.Database.CreateExecutionStrategy(), id, null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A session's staged version: staged, and with the length a session keeps, which an upload's doesn't. None for a
    /// name or ID the database can't hold.
    /// </summary>
    private IQueryable<ResourceModel> Session(ResourceDbContext dbContext, ResourceAddress resource, string versionId) =>
        HasNul(versionId)
            ? dbContext.Resources.Where(r => false)
            : Versions(dbContext.Resources.IgnoreQueryFilters(), resource.ContainerName, resource.ResourceName)
                .Where(r => r.VersionId == versionId && r.StagedAt != null && r.ContentLength != null);

    private static ResourceUploadOffsetException OffsetNotAtEnd(ResourceAddress resource, string versionId, long offset,
        long stored) =>
        new($"The upload {versionId} of the resource {resource.ResourceName} in the container {resource.ContainerName} " +
            $"has {stored} bytes stored, not {offset}.", stored)
        {
            ContainerName = resource.ContainerName, ResourceName = resource.ResourceName
        };

    /// <summary>What <see cref="CompleteStagedVersionAsync"/> reads of a session, named as its query's columns.</summary>
    internal sealed class SessionRow
    {
        public long Id { get; set; }
        public long ContentLength { get; set; }
        public DateTimeOffset StagedAt { get; set; }
    }

    public override async Task<int> DeleteAbandonedUploadsAsync(DateTimeOffset staleBefore,
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
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (long resourceId in abandoned)
            {
                try
                {
                    if (await DeleteStagedVersionAsync(strategy, resourceId, staleBefore, cancellationToken).ConfigureAwait(false)) deleted++;
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

        CountAbandonedUploads(deleted);
        if (failures.Count > 0)
        {
            throw new AggregateException($"{failures.Count} abandoned uploads' versions weren't deleted, and stay for " +
                $"the next sweep; {deleted} were.", failures);
        }
        return deleted;
    }

    /// <summary>A version about to be stored, not yet current, stamped now.</summary>
    private ResourceModel NewVersion(NewResourceVersion version) => new()
    {
        ContainerName = version.Resource.ContainerName,
        Name = version.Resource.ResourceName,
        VersionId = version.VersionId,
        LastModified = Now(),
        IsLatest = false,
        ContentType = version.ContentType,
        Metadata = new Dictionary<string, string>(version.Metadata),
        CopiedFromContainerName = version.CopiedFrom?.ContainerName,
        CopiedFromName = version.CopiedFrom?.ResourceName,
        CopiedFromVersionId = version.CopiedFrom?.VersionId,
        ExpiresAt = version.ExpiresAt,
        RetainUntil = version.RetainUntil,
        LegalHold = version.LegalHold
    };

    /// <summary>
    /// Throws <see cref="ResourceContainerNotFoundException"/> for a foreign key violation a missing container caused.
    /// Only a missing container is ours to report; the caller rethrows any other violation as the database error it is.
    /// </summary>
    private async Task ThrowIfContainerMissingAsync(string resourceContainerName, Exception exception, CancellationToken cancellationToken)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        if (!await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken).ConfigureAwait(false))
            throw ContainerNotFound(resourceContainerName, exception);
    }

    private static ResourceContainerNotFoundException ContainerNotFound(string resourceContainerName,
        Exception? innerException = null)
    {
        string message = $"The container {resourceContainerName} doesn't exist.";
        return innerException is null
            ? new(message) { ContainerName = resourceContainerName }
            : new(message, innerException) { ContainerName = resourceContainerName };
    }

    /// <summary>
    /// Throws, before anything is written, for a version the database can't store: one in a container whose name has
    /// U+0000, which can't exist, or with U+0000 in its name, version ID, content type or origin.
    /// </summary>
    private void ThrowIfUnstorable(NewResourceVersion version)
    {
        (string resourceContainerName, string resourceName) = version.Resource;
        if (HasNul(resourceContainerName)) throw ContainerNotFound(resourceContainerName);
        if (HasNul(resourceName) || HasNul(version.VersionId) || HasNul(version.ContentType)
            || version.CopiedFrom is { } origin
                && (HasNul(origin.ContainerName) || HasNul(origin.ResourceName) || HasNul(origin.VersionId)))
        {
            throw new ArgumentException($"The version of {resourceName} has U+0000 in its name, version ID, content " +
                $"type or origin, which {DatabaseRefusingNul} can't store.", nameof(version));
        }
    }

    public override async Task<WrittenContent?> CopyResourceAsync(ResourceAddress source, string? sourceVersionId,
        NewResourceVersion destination, ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default)
    {
        ThrowIfUnstorable(destination);
        (string destinationContainer, string destinationName) = destination.Resource;
        condition ??= new ResourceWriteCondition();
        // A version stored before hashes were kept has none to copy, so its content is read, and hashed, as an upload's is.
        if (MayHoldUnmeasuredVersions)
        {
            using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
            var found = await Version(dbContext.Resources, source.ContainerName, source.ResourceName, sourceVersionId)
                .Select(r => new { r.ContentHash })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (found is null) return null;
            if (found.ContentHash is null)
            {
                Stream? content = await GetResourceStreamAsync(source, sourceVersionId, cancellationToken).ConfigureAwait(false);
                if (content is null) return null;
                await using var contentDisposal = content.ConfigureAwait(false);
                return await AddResourceAsync(destination, content, condition, cancellationToken).ConfigureAwait(false);
            }
        }

        WrittenContent? written = null;
        try
        {
            return await WriteNamesAsync([source, destination.Resource], async (attempt, ct) =>
            {
                // A retry after a commit whose acknowledgment was lost finds the copy already stored.
                if (await attempt.Resources.AnyAsync(r => r.ContainerName == destinationContainer && r.Name == destinationName
                    && r.VersionId == destination.VersionId, ct).ConfigureAwait(false))
                {
                    return written
                        ?? throw VersionExists(destinationContainer, destinationName, destination.VersionId);
                }

                // Read under the source name's lock, so the version can't be deleted while its chunks are copied.
                StoredVersion? from = await StoredVersionAsync(
                    Version(attempt.Resources, source.ContainerName, source.ResourceName, sourceVersionId), ct).ConfigureAwait(false);
                if (from is null) return null;

                List<ResourceModel> priorLatest =
                    await CurrentForWriteAsync(attempt, destinationContainer, destinationName, condition, ct).ConfigureAwait(false);

                ResourceModel copy = NewVersion(destination);
                attempt.Resources.Add(copy);
                await attempt.SaveChangesAsync(ct).ConfigureAwait(false);
                // Copied by the server, so no content passes through the application.
                await CopyChunksAsync(attempt, from.Id, copy.Id, ct).ConfigureAwait(false);

                var stored = new WrittenContent(from.Length, from.ContentHash!, Now());
                await MakeCurrentAsync(attempt, priorLatest, copy, stored, ct).ConfigureAwait(false);
                return written = stored;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsForeignKeyViolation(exception))
        {
            await ThrowIfContainerMissingAsync(destinationContainer, exception, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public override async Task<bool> MoveResourceAsync(ResourceAddress source, ResourceAddress destination, ResourceConditions? sourceConditions,
        CancellationToken cancellationToken = default)
    {
        if (HasNul(source.ContainerName) || HasNul(source.ResourceName)) return false;
        if (HasNul(destination.ContainerName)) throw ContainerNotFound(destination.ContainerName);
        bool moved = false;
        try
        {
            return await WriteNamesAsync([source, destination], async (attempt, ct) =>
            {
                ResourceModel? current = await attempt.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.ContainerName == source.ContainerName
                    && r.Name == source.ResourceName && r.IsLatest, ct).ConfigureAwait(false);
                // A retry after a commit whose acknowledgment was lost finds the resource already moved.
                if (current is null) return moved;
                if (sourceConditions is not null)
                {
                    sourceConditions.ThrowIfNotMet(source, current.VersionId, current.ContentHash);
                }
                if (await attempt.Resources.AnyAsync(r => r.ContainerName == destination.ContainerName && r.Name == destination.ResourceName, ct).ConfigureAwait(false))
                    throw ResourceExists(destination.ContainerName, destination.ResourceName);

                // Only the versions' container and name change: their chunks hang off their IDs, so no content is copied.
                await attempt.Resources
                    .Where(r => r.ContainerName == source.ContainerName && r.Name == source.ResourceName)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.ContainerName, destination.ContainerName)
                        .SetProperty(r => r.Name, destination.ResourceName), ct).ConfigureAwait(false);
                await IndexNameAsync(attempt, source.ContainerName, source.ResourceName, ct).ConfigureAwait(false);
                await IndexNameAsync(attempt, destination.ContainerName, destination.ResourceName, ct).ConfigureAwait(false);
                return moved = true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsForeignKeyViolation(exception))
        {
            await ThrowIfContainerMissingAsync(destination.ContainerName, exception, cancellationToken).ConfigureAwait(false);
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
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (condition.CreateOnly && priorLatest.Count > 0)
            throw ResourceExists(resourceContainerName, resourceName);
        if (condition.Conditions is { } conditions)
        {
            ResourceModel? current = priorLatest.FirstOrDefault();
            conditions.ThrowIfNotMet(new ResourceAddress(resourceContainerName, resourceName), current?.VersionId,
                current?.ContentHash);
        }
        return priorLatest;
    }

    /// <summary>
    /// Makes <paramref name="version"/> its name's current version, with the length, hash and time
    /// <paramref name="written"/> gives. The old current version is cleared and saved first, so the unique "latest"
    /// index never holds two rows for the name in the middle of the write.
    /// </summary>
    private async Task MakeCurrentAsync(ResourceDbContext attempt, List<ResourceModel> priorLatest,
        ResourceModel version, WrittenContent written, CancellationToken cancellationToken)
    {
        foreach (ResourceModel prior in priorLatest) prior.IsLatest = false;
        await attempt.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        version.IsLatest = true;
        version.ContentLength = written.ContentLength;
        version.ContentHash = written.ContentHash;
        version.LastModified = written.LastModified;
        await attempt.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await IndexNameAsync(attempt, version.ContainerName, version.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the name's searchable metadata entries its current version's, in the caller's transaction, once a change
    /// that made a version current, or changed its metadata, is saved. Nothing to do without
    /// <see cref="ResourceRepository.SearchableMetadataKeys"/>.
    /// </summary>
    private async Task IndexNameAsync(ResourceDbContext dbContext, string resourceContainerName, string resourceName,
        CancellationToken cancellationToken)
    {
        if (SearchableMetadataKeys.Count == 0) return;
        List<ResourceMetadataModel> indexed = await dbContext.ResourceMetadata
            .Where(m => m.ContainerName == resourceContainerName && m.Name == resourceName)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var current = await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.IsLatest)
            .Select(r => new { r.Id, r.Metadata })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // Changed in place where a row can stay, since removing a row and adding one with its key can't be tracked at once.
        Dictionary<string, string> wanted = current is null ? [] : SearchableEntries(current.Metadata);
        foreach (ResourceMetadataModel entry in indexed)
        {
            if (entry.ResourceId == current?.Id && wanted.Remove(entry.Key, out string? value)) entry.Value = value;
            else dbContext.ResourceMetadata.Remove(entry);
        }
        foreach ((string key, string value) in wanted)
        {
            dbContext.ResourceMetadata.Add(new ResourceMetadataModel
            {
                ResourceId = current!.Id, ContainerName = resourceContainerName, Name = resourceName, Key = key, Value = value
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The entries of <paramref name="metadata"/> a search can find: those of <see cref="ResourceRepository.SearchableMetadataKeys"/>,
    /// leaving out a value longer than a search can look for, which a version stored before its key was named can have,
    /// and any with U+0000, which some databases can't store. Nothing is lost: no search can look for either.
    /// </summary>
    private Dictionary<string, string> SearchableEntries(IDictionary<string, string> metadata) =>
        SearchableMetadataKeys.Where(key => metadata.TryGetValue(key, out string? value)
                && value.Length <= ResourceMetadataMatch.MaxValueLength && !HasNul(key) && !HasNul(value))
            .ToDictionary(key => key, key => metadata[key], StringComparer.Ordinal);

    /// <summary>
    /// Inserts a batch of chunks in one statement, committed as it runs, and empties the batch; with
    /// <paramref name="stamp"/>, the same round trip moves the version's <see cref="ResourceModel.StagedAt"/> forward.
    /// After a transient failure the strategy sends the batch again from the same buffers, and a retry after a commit
    /// whose acknowledgment was lost, refused because the chunks are already stored, has nothing left to do. The
    /// command is sent straight through the database's driver: tracking each chunk, or sending the command through EF
    /// Core's raw SQL, more than doubled what an upload allocated on SQL Server.
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
            DateTimeOffset now = Now();

            if (!dbContext.Database.IsRelational())
            {
                dbContext.ResourceChunks.AddRange(batch);
                if (stamp)
                {
                    (await dbContext.Resources.IgnoreQueryFilters().SingleAsync(r => r.Id == resourceId, ct).ConfigureAwait(false)).StagedAt =
                        now;
                }
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                return;
            }

            await dbContext.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                DbCommand command = NewCommand(dbContext);
                await using var commandDisposal = command.ConfigureAwait(false);
                command.CommandText = ResourceDbContext.QualifyTables(InsertChunks(command, batch, stamp ? now : null),
                    dbContext.Schema);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (DbException exception) when (retry && IsUniqueViolation(exception))
            {
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private Task<T> WriteNameAsync<T>(string resourceContainerName, string resourceName,
        Func<ResourceDbContext, CancellationToken, Task<T>> write, CancellationToken cancellationToken) =>
        WriteNamesAsync([new ResourceAddress(resourceContainerName, resourceName)], write, cancellationToken);

    /// <summary>
    /// Runs a write to resource names in a transaction holding each name's write lock, so writers of a name queue
    /// rather than race. The locks are taken in one order whatever the order of the names, so two writes to the same
    /// two names can't deadlock. Each attempt gets a fresh context so the execution strategy can retry all of it after a
    /// transient failure.
    /// </summary>
    private async Task<T> WriteNamesAsync<T>(IEnumerable<ResourceAddress> names,
        Func<ResourceDbContext, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        List<ResourceAddress> ordered = [.. names.Distinct()
            .OrderBy(n => n.ContainerName, StringComparer.Ordinal).ThenBy(n => n.ResourceName, StringComparer.Ordinal)];

        return await dbContext.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            using ResourceDbContext attempt = NewWriteContext();
            var transaction = await attempt.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var transactionDisposal = transaction.ConfigureAwait(false);

            // The in-memory provider the unit tests use has no locks.
            if (attempt.Database.IsRelational()) await LockNamesAsync(attempt, ordered, ct).ConfigureAwait(false);

            T result = await write(attempt, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }, cancellationToken).ConfigureAwait(false);
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

    private async Task ThrowIfNotMetAsync(ResourceDbContext dbContext, ResourceConditions conditions,
        string resourceContainerName, string resourceName, CancellationToken cancellationToken)
    {
        if (conditions.IsEmpty) return;

        var current = await Version(dbContext.Resources, resourceContainerName, resourceName, null)
            .Select(r => new { r.VersionId, r.ContentHash })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        conditions.ThrowIfNotMet(new ResourceAddress(resourceContainerName, resourceName), current?.VersionId,
            current?.ContentHash);
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

    public override async Task<IReadOnlyList<ResourceInfo>> GetResourcePageAsync(string resourceContainerName, string? prefix,
        string? afterName, int pageSize, CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName) || HasNul(prefix)) return [];
        afterName = CutAtNul(afterName);
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // A seek on the current-versions index, which holds (ContainerName, Name) in the names' binary order: the
        // prefix and the name to continue after bound the range, so a page reads only its own rows.
        IQueryable<ResourceModel> current = dbContext.Resources.Where(r => r.ContainerName == resourceContainerName && r.IsLatest);
        if (prefix is not null) current = WithNamePrefix(current, prefix, afterName);
        if (afterName is not null) current = current.Where(r => string.Compare(r.Name, afterName) > 0);

        return await current
            .OrderBy(r => r.Name)
            .Take(pageSize)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<ResourceInfo>> FindResourcePageAsync(string resourceContainerName, string? prefix,
        string? afterName, int pageSize, ResourceMetadataMatch match, CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName) || HasNul(prefix) || HasNul(match.Key) || HasNul(match.Value)) return [];
        afterName = CutAtNul(afterName);
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // A seek on the search index, which holds (ContainerName, Key, Value, Name), so a page reads only its matches. An
        // index row counts only while its version is still the current one at that container and name. SQL Server
        // compares as if the shorter string were padded with spaces, so "42 " equals "42" there: each version found is
        // checked exactly here, and another batch follows if that leaves a page short.
        var found = new List<ResourceInfo>(pageSize);
        while (found.Count < pageSize)
        {
            IQueryable<ResourceMetadataModel> entries = dbContext.ResourceMetadata
                .Where(m => m.ContainerName == resourceContainerName && m.Key == match.Key && m.Value == match.Value);
            if (afterName is not null) entries = entries.Where(m => string.Compare(m.Name, afterName) > 0);
            IQueryable<ResourceModel> current = entries.Join(dbContext.Resources.Where(r => r.IsLatest), m => m.ResourceId, r => r.Id,
                (m, r) => new { m, r }).Where(x => x.r.ContainerName == x.m.ContainerName && x.r.Name == x.m.Name).Select(x => x.r);
            if (prefix is not null) current = WithNamePrefix(current, prefix, afterName);
            int batch = pageSize - found.Count;
            List<ResourceInfo> page = await current
                .OrderBy(r => r.Name)
                .Take(batch)
                .Select(AsResourceInfo)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            found.AddRange(page.Where(r => match.IsMatch(r.Metadata)));
            if (page.Count < batch) break;
            afterName = page[^1].Name;
        }
        return found;
    }

    /// <summary>How many names <see cref="IndexMetadataAsync"/> indexes in one transaction.</summary>
    private const int NamesPerIndexBatch = 100;

    public override async Task<int> IndexMetadataAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        if (SearchableMetadataKeys.Count == 0 || HasNul(resourceContainerName)) return 0;
        int indexed = 0;
        string? afterName = null;
        while (true)
        {
            List<string> names;
            using (ResourceDbContext dbContext = dbContextFactory.CreateDbContext())
            {
                IQueryable<ResourceModel> current = dbContext.Resources.Where(r => r.ContainerName == resourceContainerName && r.IsLatest);
                if (afterName is not null) current = current.Where(r => string.Compare(r.Name, afterName) > 0);
                names = await current.OrderBy(r => r.Name).Take(NamesPerIndexBatch).Select(r => r.Name)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
            }
            if (names.Count == 0) return indexed;

            // Under the names' locks, so a write to one of them can't come between reading its metadata and indexing it.
            await WriteNamesAsync(names.Select(name => new ResourceAddress(resourceContainerName, name)), async (dbContext, ct) =>
            {
                foreach (string name in names) await IndexNameAsync(dbContext, resourceContainerName, name, ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
            indexed += names.Count;
            afterName = names[^1];
        }
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceAddress resource,
        CancellationToken cancellationToken = default)
    {
        (string resourceContainerName, string resourceName) = resource;
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await Versions(dbContext.Resources, resourceContainerName, resourceName)
            .OrderByDescending(r => r.IsLatest)
            .ThenByDescending(r => r.Id)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetVersionPageAsync(string resourceContainerName, string? afterName,
        int maxNames, CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName)) return [];
        afterName = CutAtNul(afterName);
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
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<ResourceInfo?> GetResourceInfoAsync(ResourceAddress resource, string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        (string resourceContainerName, string resourceName) = resource;
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        ResourceModel? existingResource = await ResolveAsync(dbContext.Resources, resourceContainerName, resourceName, versionId, cancellationToken).ConfigureAwait(false);

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
        Metadata = (IReadOnlyDictionary<string, string>)r.Metadata,
        CopiedFrom = r.CopiedFromVersionId == null ? null
            : new ResourceOrigin(r.CopiedFromContainerName!, r.CopiedFromName!, r.CopiedFromVersionId),
        ExpiresAt = r.ExpiresAt,
        RetainUntil = r.RetainUntil,
        LegalHold = r.LegalHold,
        DeletedAt = r.DeletedAt,
        PurgeAt = r.PurgeAt
    };

    /// <summary>A stored version already read, as the clients see it.</summary>
    private static readonly Func<ResourceModel, ResourceInfo> ToResourceInfo = AsResourceInfo.Compile();

    public override Task<Stream?> GetResourceStreamAsync(ResourceAddress resource, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        GetResourceStreamAsync(resource, versionId, 0, null, cancellationToken);

    public override async Task<Stream?> GetResourceStreamAsync(ResourceAddress resource, string? versionId, long offset,
        long? length, CancellationToken cancellationToken = default)
    {
        (string resourceContainerName, string resourceName) = resource;
        // The stream reads the chunks through this context as the caller reads, and disposes it.
        ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        try
        {
            // One statement, so the length belongs to the version found even if it is deleted right after.
            StoredVersion? existingResource = await StoredVersionAsync(
                Version(dbContext.Resources, resourceContainerName, resourceName, versionId), cancellationToken).ConfigureAwait(false);
            if (existingResource == null)
            {
                await dbContext.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            long start = Math.Min(offset, existingResource.Length);
            long end = length < existingResource.Length - start ? start + length.Value : existingResource.Length;
            if (start == 0 && end == existingResource.Length)
                return new ResourceStream(PageReader(dbContext, existingResource.Id), existingResource.Length, dbContext);

            // Only the chunks the range overlaps, found by adding up their lengths: releases have stored different
            // chunk sizes, so a position can't be turned into an index by division.
            ChunkSpan chunks = await ChunkSpanAsync(dbContext, existingResource.Id, start, end, cancellationToken).ConfigureAwait(false);
            return new ResourceStream(PageReader(dbContext, existingResource.Id, chunks.AfterIndex, chunks.ThroughIndex),
                existingResource.Length, dbContext, chunks.Start ?? 0, start, end);
        }
        catch
        {
            await dbContext.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads a version's chunks a page at a time, each page its own short query, retried as a whole after a transient
    /// failure: every chunk, or for a range those after <paramref name="afterIndex"/> through
    /// <paramref name="throughIndex"/>. The data streams from the reader into buffers the pages share, rather than a new
    /// array per chunk, so a page's data is valid only until the next page is read.
    /// </summary>
    internal Func<CancellationToken, Task<IReadOnlyList<ReadOnlyMemory<byte>>>> PageReader(ResourceDbContext dbContext,
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
                    DbCommand command = NewCommand(dbContext);
                    await using var commandDisposal = command.ConfigureAwait(false);
                    command.CommandText = ResourceDbContext.QualifyTables(ReadChunks(command, resourceId, lastIndex, throughIndex),
                        dbContext.Schema);

                    // Sequential access streams each chunk's data instead of buffering the row first.
                    DbDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
                    await using var readerDisposal = reader.ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        pageLastIndex = reader.GetInt32(0);
                        int dataLength = (int)reader.GetInt64(1);
                        byte[] buffer = buffers[page.Count] is { } existing && existing.Length >= dataLength
                            ? existing
                            : buffers[page.Count] = new byte[Math.Max(dataLength, ResourceChunker.ChunkSize)];
                        Stream data = reader.GetStream(2);
                        await using var dataDisposal = data.ConfigureAwait(false);
                        try
                        {
                            await data.ReadExactlyAsync(buffer.AsMemory(0, dataLength), ct).ConfigureAwait(false);
                        }
                        catch (IOException exception) when (exception.InnerException is DbException databaseException)
                        {
                            // The column stream wraps a failure to read from the server; unwrapped, the execution
                            // strategy can recognize a transient one and read the page again, and a caller doesn't
                            // mistake it for content cut short by a delete.
                            ExceptionDispatchInfo.Throw(databaseException);
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

    public override Task<bool> DeleteResourceAsync(ResourceAddress resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default)
    {
        (string resourceContainerName, string resourceName) = resource;
        conditions ??= new ResourceConditions();
        if (versionId == null) return DeleteNameAsync(resourceContainerName, resourceName, conditions, cancellationToken);

        bool deletedInAnEarlierAttempt = false;

        // Under the name's lock, so a concurrent delete of the same version finds it gone instead of failing, the
        // promotion of the next version can't race a write, and the conditions hold until the delete commits.
        return WriteNameAsync(resourceContainerName, resourceName, async (dbContext, ct) =>
        {
            // A retry after a commit whose acknowledgment was lost finds what it deleted gone. The conditions it met
            // no longer hold then, and it mustn't report that nothing was deleted.
            if (deletedInAnEarlierAttempt && !await dbContext.Resources.AnyAsync(r => r.ContainerName == resourceContainerName
                && r.Name == resourceName && r.VersionId == versionId, ct).ConfigureAwait(false))
                return true;

            await ThrowIfNotMetAsync(dbContext, conditions, resourceContainerName, resourceName, ct).ConfigureAwait(false);
            if (await ResolveWithChunksAsync(dbContext.Resources, resourceContainerName, resourceName, versionId, ct).ConfigureAwait(false) is not { } found)
                return deletedInAnEarlierAttempt = false;
            ThrowIfRetained(found.Version, Now());
            return deletedInAnEarlierAttempt = await DeleteVersionAsync(dbContext, found.Version, found.Chunks, ct).ConfigureAwait(false);
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
                // A retry after a commit whose acknowledgment was lost keeps the list it took, and doesn't check the
                // conditions again: with versions deleted, they may no longer hold.
                bool firstBatch = versions.Count == 0;
                if (firstBatch)
                {
                    await ThrowIfNotMetAsync(dbContext, conditions, resourceContainerName, resourceName, ct).ConfigureAwait(false);
                    var listed = await Versions(dbContext.Resources, resourceContainerName, resourceName)
                        .OrderBy(r => r.IsLatest).ThenBy(r => r.Id)
                        .Select(r => new { r.Id, r.VersionId, r.RetainUntil, r.LegalHold, Chunks = r.Chunks!.Count })
                        .ToListAsync(ct).ConfigureAwait(false);
                    // Every version is checked before any goes, so a delete that's refused deletes nothing.
                    DateTimeOffset now = Now();
                    if (listed.FirstOrDefault(v => v.LegalHold || v.RetainUntil > now) is { } retained)
                    {
                        throw ResourceRetainedException.Create(new ResourceAddress(resourceContainerName, resourceName),
                            retained.VersionId!, retained.RetainUntil, retained.LegalHold, now);
                    }
                    versions = [.. listed.Select(v => (v.Id, v.Chunks))];
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
                        .ToListAsync(ct).ConfigureAwait(false);
                    // This batch took the name's lock again after the first one released it, so a hold or a later date
                    // may have been set in between.
                    DateTimeOffset now = Now();
                    foreach (ResourceModel version in found) ThrowIfRetained(version, now);
                }
                await DeleteLargeVersionsChunksAsync(dbContext, found.Select(r => (r.Id, batch.First(v => v.Id == r.Id).Chunks)), ct).ConfigureAwait(false);
                dbContext.Resources.RemoveRange(found);
                deletedAny |= found.Count > 0;
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                return batch.Count;
            }, cancellationToken).ConfigureAwait(false);
        }
        while (deleted < versions.Count);

        return deletedAny;
    }

    /// <summary>
    /// Deletes, <see cref="ChunksDeletedPerStatement"/> at a time, the chunks of versions with more than that many, before
    /// the versions go. Smaller versions' chunks go with them, through the cascade, without a round trip apiece.
    /// </summary>
    private async Task DeleteLargeVersionsChunksAsync(ResourceDbContext dbContext, IEnumerable<(long Id, int Chunks)> versions,
        CancellationToken cancellationToken)
    {
        foreach ((long id, int chunks) in versions)
        {
            if (chunks > ChunksDeletedPerStatement) await DeleteChunksAsync(dbContext, id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a version's chunks a statement at a time, in the caller's transaction, before the version itself goes:
    /// see <see cref="ChunksDeletedPerStatement"/> for why the cascade doesn't take them all at once.
    /// </summary>
    private async Task DeleteChunksAsync(ResourceDbContext dbContext, long resourceId, CancellationToken cancellationToken)
    {
        // The in-memory provider the unit tests use leaves them to the cascade.
        if (!dbContext.Database.IsRelational()) return;

        while (await DeleteSomeChunksAsync(dbContext, resourceId, cancellationToken).ConfigureAwait(false) == ChunksDeletedPerStatement)
        {
        }
    }

    /// <summary>
    /// Deletes one version, under its name's lock; when it's the current version, the most recently stored of the rest
    /// becomes current.
    /// </summary>
    private async Task<bool> DeleteVersionAsync(ResourceDbContext dbContext, ResourceModel version, int chunks, CancellationToken cancellationToken)
    {
        await DeleteLargeVersionsChunksAsync(dbContext, [(version.Id, chunks)], cancellationToken).ConfigureAwait(false);
        dbContext.Resources.Remove(version);

        if (version.IsLatest)
        {
            ResourceModel? next = await dbContext.Resources
                .Where(r => r.ContainerName == version.ContainerName && r.Name == version.Name && r.Id != version.Id)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (next != null) next.IsLatest = true;
        }

        bool deleted = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false) > 0;
        if (version.IsLatest) await IndexNameAsync(dbContext, version.ContainerName, version.Name, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public override bool SupportsExpiry => true;

    public override async Task<IReadOnlyList<ResourceInfo>> GetExpiredAsync(DateTimeOffset now, int maxVersions,
        (DateTimeOffset ExpiresAt, string VersionId)? after = null, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // A seek on the index of versions that expire, so a sweep with nothing to delete reads only that.
        IQueryable<ResourceModel> expired = dbContext.Resources.Where(r => r.ExpiresAt != null && r.ExpiresAt <= now
            && !r.LegalHold && (r.RetainUntil == null || r.RetainUntil <= now));
        if (after is { } last)
        {
            (DateTimeOffset afterExpiry, string afterVersion) = last;
            // Written as a range on the index's first column, so it seeks to where the last batch ended.
            expired = expired.Where(r => r.ExpiresAt >= afterExpiry
                && (r.ExpiresAt > afterExpiry || string.Compare(r.VersionId, afterVersion) > 0));
        }

        return await expired
            .OrderBy(r => r.ExpiresAt)
            .ThenBy(r => r.VersionId)
            .Take(maxVersions)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task<bool> DeleteExpiredVersionAsync(ResourceAddress resource, string versionId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        bool deletedInAnEarlierAttempt = false;

        // Under the name's lock, so a change to the expiry can't come between the check and the delete.
        return WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
        {
            // A retry after a commit whose acknowledgment was lost finds the version gone.
            if (await ResolveWithChunksAsync(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId, ct).ConfigureAwait(false) is not { } found)
                return deletedInAnEarlierAttempt;
            if (!(found.Version.ExpiresAt <= now) || !IsDeletable(found.Version, now)) return false;
            return deletedInAnEarlierAttempt = await DeleteVersionAsync(dbContext, found.Version, found.Chunks, ct).ConfigureAwait(false);
        }, cancellationToken);
    }

    public override async Task<IReadOnlyCollection<(ResourceAddress Resource, string VersionId)>> DeleteExpiredVersionsAsync(
        IReadOnlyList<(ResourceAddress Resource, string VersionId)> versions, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var deleted = new List<(ResourceAddress, string)>();
        // The expired versions left for a later batch, with their chunks.
        List<(ResourceAddress Resource, string VersionId, int Chunks)> later = [];
        List<(ResourceAddress Resource, string VersionId)> batch = [.. versions.Distinct()];
        while (batch.Count > 0)
        {
            // What an earlier attempt deleted, should a retry follow a commit whose acknowledgment was lost.
            List<(ResourceAddress, string)> deletedInAnEarlierAttempt = [];

            // A transaction under the batch's names' locks, so a change to an expiry can't come between the check and
            // the delete. It deletes up to ChunksPerDeleteBatch chunks' worth, as a container or name delete does, and
            // leaves the rest of the expired versions for the next.
            List<(ResourceAddress Resource, string VersionId)> current = batch;
            (List<(ResourceAddress, string)> gone, List<(ResourceAddress, string, int)> left) = await WriteNamesAsync(
                current.Select(v => v.Resource),
                async (dbContext, ct) =>
                {
                    var result = await DeleteExpiredBatchAsync(dbContext, current, now, deletedInAnEarlierAttempt, ct).ConfigureAwait(false);
                    deletedInAnEarlierAttempt = result.Deleted;
                    return result;
                }, cancellationToken).ConfigureAwait(false);
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
    private async Task<(List<(ResourceAddress, string)> Deleted, List<(ResourceAddress, string, int)> Left)>
        DeleteExpiredBatchAsync(ResourceDbContext dbContext, List<(ResourceAddress Resource, string VersionId)> versions,
            DateTimeOffset now, List<(ResourceAddress, string)> deletedInAnEarlierAttempt, CancellationToken ct)
    {
        string[] ids = [.. versions.Select(v => v.VersionId).Distinct()];
        string[] batchContainers = [.. versions.Select(v => v.Resource.ContainerName).Distinct()];
        string[] batchNames = [.. versions.Select(v => v.Resource.ResourceName).Distinct()];
        var wanted = versions.ToHashSet();
        // By container and name first, so it seeks on the index of (ContainerName, Name, VersionId) rather than
        // scanning the table.
        var found = (await dbContext.Resources
                .Where(r => batchContainers.Contains(r.ContainerName) && batchNames.Contains(r.Name)
                    && ids.Contains(r.VersionId))
                .Select(r => new { Version = r, Chunks = r.Chunks!.Count })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(f => wanted.Contains(
                (new ResourceAddress(f.Version.ContainerName, f.Version.Name), f.Version.VersionId!)))
            .ToList();
        var expired = found.Where(f => f.Version.ExpiresAt <= now && IsDeletable(f.Version, now)).ToList();
        var deleting = expired.Take(CountWithinDeleteBudget(expired.Select(e => e.Chunks))).ToList();

        await DeleteLargeVersionsChunksAsync(dbContext, deleting.Select(d => (d.Version.Id, d.Chunks)), ct).ConfigureAwait(false);
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
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (ResourceModel version in next.Where(v => lostCurrent.Contains((v.ContainerName, v.Name))))
                version.IsLatest = true;
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach ((string containerName, string name) in lostCurrent)
            await IndexNameAsync(dbContext, containerName, name, ct).ConfigureAwait(false);

        // A version gone already counts if an earlier attempt deleted it.
        static (ResourceAddress, string) Key(ResourceModel version) =>
            (new ResourceAddress(version.ContainerName, version.Name), version.VersionId!);
        var foundKeys = found.Select(f => Key(f.Version)).ToHashSet();
        return ([.. deleting.Select(d => Key(d.Version)), .. deletedInAnEarlierAttempt.Where(v => !foundKeys.Contains(v))],
            [.. expired.Skip(deleting.Count).Select(e => (new ResourceAddress(e.Version.ContainerName, e.Version.Name),
                e.Version.VersionId!, e.Chunks))]);
    }

    public override Task<bool> SetExpiryAsync(ResourceAddress resource, string? versionId, DateTimeOffset? expiresAt,
        CancellationToken cancellationToken = default) =>
        WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
            await Version(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, expiresAt), ct).ConfigureAwait(false) > 0, cancellationToken);

    public override bool SupportsRetention => true;

    // Under the name's lock, as every delete is, so a change and a delete of the version can't interleave.
    public override Task<bool> SetRetentionAsync(ResourceAddress resource, string? versionId, DateTimeOffset retainUntil,
        CancellationToken cancellationToken = default) =>
        WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
        {
            IQueryable<ResourceModel> version = Version(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId);
            if (await version.Select(r => new { r.VersionId, r.RetainUntil }).FirstOrDefaultAsync(ct).ConfigureAwait(false) is not { } stored)
                return false;
            if (stored.RetainUntil > retainUntil)
            {
                throw new ArgumentOutOfRangeException(nameof(retainUntil), retainUntil, $"The version {stored.VersionId} of the resource " +
                    $"{resource.ResourceName} in the container {resource.ContainerName} is retained until {stored.RetainUntil:O}, and that can't move earlier.");
            }
            return await version.ExecuteUpdateAsync(s => s.SetProperty(r => r.RetainUntil, retainUntil), ct).ConfigureAwait(false) > 0;
        }, cancellationToken);

    public override Task<bool> SetLegalHoldAsync(ResourceAddress resource, string? versionId, bool hold,
        CancellationToken cancellationToken = default) =>
        WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
            await Version(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LegalHold, hold), ct).ConfigureAwait(false) > 0, cancellationToken);

    public override bool SupportsSoftDelete => true;

    /// <summary>
    /// Marks the versions deleted, in one transaction under the name's lock that decides the conditions and retention,
    /// and makes the most recently stored remaining version current if the current one went. Nothing is removed, so
    /// unlike a delete of a name with many versions, it needs no batches.
    /// </summary>
    public override Task<bool> SoftDeleteResourceAsync(ResourceAddress resource, string? versionId, ResourceConditions? conditions,
        DateTimeOffset purgeAt, CancellationToken cancellationToken = default)
    {
        (string resourceContainerName, string resourceName) = resource;
        bool deletedInAnEarlierAttempt = false;
        return WriteNameAsync(resourceContainerName, resourceName, async (dbContext, ct) =>
        {
            IQueryable<ResourceModel> targets = versionId is null
                ? Versions(dbContext.Resources, resourceContainerName, resourceName)
                : Version(dbContext.Resources, resourceContainerName, resourceName, versionId);
            // A retry after a commit whose acknowledgment was lost finds what it deleted in the bin. The conditions it
            // met no longer hold then, and it mustn't report that nothing was deleted.
            if (deletedInAnEarlierAttempt && !await targets.AnyAsync(ct).ConfigureAwait(false)) return true;

            await ThrowIfNotMetAsync(dbContext, conditions ?? new ResourceConditions(), resourceContainerName, resourceName, ct).ConfigureAwait(false);
            List<ResourceModel> deleting = await targets.ToListAsync(ct).ConfigureAwait(false);
            if (deleting.Count == 0) return deletedInAnEarlierAttempt = false;
            DateTimeOffset now = Now();
            foreach (ResourceModel version in deleting) ThrowIfRetained(version, now);

            bool lostCurrent = deleting.Any(v => v.IsLatest);
            foreach (ResourceModel version in deleting)
            {
                version.DeletedAt = now;
                version.PurgeAt = purgeAt;
                version.IsLatest = false;
            }
            // Saved before another version becomes current, so the index of current versions never holds two.
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            if (lostCurrent)
            {
                ResourceModel? next = await Versions(dbContext.Resources, resourceContainerName, resourceName)
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (next is not null)
                {
                    next.IsLatest = true;
                    await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                await IndexNameAsync(dbContext, resourceContainerName, resourceName, ct).ConfigureAwait(false);
            }
            return deletedInAnEarlierAttempt = true;
        }, cancellationToken);
    }

    /// <summary>The versions in bins, which the filter on every other query leaves out.</summary>
    private static IQueryable<ResourceModel> Bin(ResourceDbContext dbContext) =>
        dbContext.Resources.IgnoreQueryFilters().Where(r => r.DeletedAt != null);

    /// <summary>A name's versions in the bin, or one of them when <paramref name="versionId"/> is set.</summary>
    private IQueryable<ResourceModel> Binned(ResourceDbContext dbContext, ResourceAddress resource, string? versionId)
    {
        IQueryable<ResourceModel> binned = Versions(Bin(dbContext), resource.ContainerName, resource.ResourceName);
        if (versionId is null) return binned;
        return HasNul(versionId) ? binned.Where(r => false) : binned.Where(r => r.VersionId == versionId);
    }

    /// <summary>A seek on the index of versions in bins, newest deletion first.</summary>
    public override async Task<DeletedVersionPage> GetDeletedPageAsync(string resourceContainerName, string? prefix,
        (DateTimeOffset DeletedAt, string VersionId)? after, int pageSize, CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName) || HasNul(prefix)) return new DeletedVersionPage([], null);
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        IQueryable<ResourceModel> bin = Bin(dbContext).Where(r => r.ContainerName == resourceContainerName);
        if (prefix is not null) bin = WithNamePrefix(bin, prefix, null);
        if (after is { } last)
        {
            DateTimeOffset afterDeleted = last.DeletedAt;
            string? afterVersion = CutAtNul(last.VersionId);
            // Written as a range on the index's second column, so it seeks to where the last page ended.
            bin = bin.Where(r => r.DeletedAt <= afterDeleted
                && (r.DeletedAt < afterDeleted || string.Compare(r.VersionId, afterVersion) > 0));
        }

        // One more than a page, to tell whether another follows.
        List<ResourceInfo> found = await bin
            .OrderByDescending(r => r.DeletedAt)
            .ThenBy(r => r.VersionId)
            .Take(pageSize + 1)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (found.Count <= pageSize) return new DeletedVersionPage(found, null);
        found.RemoveAt(pageSize);
        return new DeletedVersionPage(found, (found[^1].DeletedAt!.Value, found[^1].VersionId));
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetDeletedVersionsAsync(ResourceAddress resource,
        CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        return await Binned(dbContext, resource, null)
            .OrderByDescending(r => r.DeletedAt)
            .ThenBy(r => r.VersionId)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the versions out of the bin under the name's lock. They keep their row IDs, which order a name's versions,
    /// so they come back among the older versions in the order they were stored.
    /// </summary>
    public override Task<int> UndeleteAsync(ResourceAddress resource, string? versionId, CancellationToken cancellationToken = default)
    {
        int undeletedInAnEarlierAttempt = 0;
        return WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
        {
            List<ResourceModel> returning = await Binned(dbContext, resource, versionId).ToListAsync(ct).ConfigureAwait(false);
            // A retry after a commit whose acknowledgment was lost finds them back already.
            if (returning.Count == 0) return undeletedInAnEarlierAttempt;

            bool hasCurrent = await Version(dbContext.Resources, resource.ContainerName, resource.ResourceName, null)
                .AnyAsync(ct).ConfigureAwait(false);
            foreach (ResourceModel version in returning)
            {
                version.DeletedAt = null;
                version.PurgeAt = null;
            }
            if (!hasCurrent) returning.MaxBy(r => r.Id)!.IsLatest = true;
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            if (!hasCurrent) await IndexNameAsync(dbContext, resource.ContainerName, resource.ResourceName, ct).ConfigureAwait(false);
            return undeletedInAnEarlierAttempt = returning.Count;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes the versions under the name's lock, in batches of up to <see cref="ChunksPerDeleteBatch"/> chunks that
    /// each commit on their own, as a delete of a name does.
    /// </summary>
    public override async Task<int> PurgeDeletedAsync(ResourceAddress resource, string? versionId, DateTimeOffset? dueBy,
        CancellationToken cancellationToken = default)
    {
        int purged = 0;
        while (true)
        {
            int purgedInAnEarlierAttempt = 0;
            (int count, bool more) = await WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
            {
                IQueryable<ResourceModel> binned = Binned(dbContext, resource, versionId);
                if (dueBy is { } due) binned = binned.Where(r => r.PurgeAt <= due);
                var found = await binned.OrderBy(r => r.Id).Select(r => new { r.Id, Chunks = r.Chunks!.Count }).ToListAsync(ct).ConfigureAwait(false);
                // A retry after a commit whose acknowledgment was lost finds what it purged gone.
                if (found.Count == 0) return (purgedInAnEarlierAttempt, false);

                var batch = found.Take(CountWithinDeleteBudget(found.Select(f => f.Chunks))).ToList();
                await DeleteLargeVersionsChunksAsync(dbContext, batch.Select(v => (v.Id, v.Chunks)), ct).ConfigureAwait(false);
                // Removed by key: they were read just now, under the name's lock.
                List<ResourceModel> versions = [.. batch.Select(v => new ResourceModel
                {
                    Id = v.Id, ContainerName = resource.ContainerName, Name = resource.ResourceName
                })];
                dbContext.Resources.AttachRange(versions);
                dbContext.Resources.RemoveRange(versions);
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                return (purgedInAnEarlierAttempt = batch.Count, batch.Count < found.Count);
            }, cancellationToken).ConfigureAwait(false);
            purged += count;
            if (!more) return purged;
        }
    }

    /// <summary>A seek on the index of versions in bins by purge time, as <see cref="GetExpiredAsync"/> seeks expiries.</summary>
    public override async Task<IReadOnlyList<ResourceInfo>> GetPurgeableAsync(DateTimeOffset now, int maxVersions,
        (DateTimeOffset PurgeAt, string VersionId)? after = null, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        IQueryable<ResourceModel> due = Bin(dbContext).Where(r => r.PurgeAt != null && r.PurgeAt <= now);
        if (after is { } last)
        {
            (DateTimeOffset afterPurge, string afterVersion) = last;
            due = due.Where(r => r.PurgeAt >= afterPurge && (r.PurgeAt > afterPurge || string.Compare(r.VersionId, afterVersion) > 0));
        }

        return await due
            .OrderBy(r => r.PurgeAt)
            .ThenBy(r => r.VersionId)
            .Take(maxVersions)
            .Select(AsResourceInfo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a stored version may be deleted at <paramref name="now"/>: it's neither held nor retained.</summary>
    private static bool IsDeletable(ResourceModel version, DateTimeOffset now) => !version.LegalHold && !(version.RetainUntil > now);

    /// <summary>Throws <see cref="ResourceRetainedException"/> if a stored version can't be deleted at <paramref name="now"/>.</summary>
    private static void ThrowIfRetained(ResourceModel version, DateTimeOffset now)
    {
        if (!IsDeletable(version, now))
        {
            throw ResourceRetainedException.Create(new ResourceAddress(version.ContainerName, version.Name), version.VersionId!,
                version.RetainUntil, version.LegalHold, now);
        }
    }

    public override Task<bool> SetResourceMetadataAsync(ResourceAddress resource, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        IDictionary<string, string> copy = new Dictionary<string, string>(metadata);
        return WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
        {
            if (await Version(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Metadata, copy), ct).ConfigureAwait(false) == 0)
            {
                return false;
            }
            await IndexNameAsync(dbContext, resource.ContainerName, resource.ResourceName, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public override Task<bool> SetContentTypeAsync(ResourceAddress resource, string? versionId, string? contentType,
        CancellationToken cancellationToken = default)
    {
        if (HasNul(contentType))
            throw new ArgumentException($"The content type has U+0000, which {DatabaseRefusingNul} can't store.", nameof(contentType));
        return WriteNameAsync(resource.ContainerName, resource.ResourceName, async (dbContext, ct) =>
            await Version(dbContext.Resources, resource.ContainerName, resource.ResourceName, versionId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ContentType, contentType), ct).ConfigureAwait(false) > 0, cancellationToken);
    }

    public override async Task AddContainerAsync(string resourceContainerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        bool created = false;
        await WriteNamesAsync([], async (attempt, ct) =>
        {
            attempt.ResourceContainers.Add(new ResourceContainerModel
            {
                Name = resourceContainerName,
                Metadata = new Dictionary<string, string>(metadata)
            });

            try
            {
                await attempt.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // A retry after a commit whose acknowledgment was lost finds the container it created. It can't tell
                // that from a commit that failed while another caller created the name, which counts as created too.
                if (created) return true;
                throw new ResourceConflictException($"The container {resourceContainerName} already exists.",
                    exception) { ContainerName = resourceContainerName };
            }

            return created = true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<bool> SetContainerMetadataAsync(string resourceContainerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName)) return false;
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        IDictionary<string, string> copy = new Dictionary<string, string>(metadata);
        return await dbContext.ResourceContainers.Where(x => x.Name == resourceContainerName)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Metadata, copy), cancellationToken).ConfigureAwait(false) > 0;
    }

    public override async Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName, int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (HasNul(prefix)) return [];
        afterName = CutAtNul(afterName);
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // Seeks on the primary key, which holds the names in their binary order, as GetResourcePageAsync does.
        IQueryable<ResourceContainerModel> containers = dbContext.ResourceContainers;
        if (prefix is not null) containers = WithContainerPrefix(containers, prefix, afterName);
        if (afterName is not null) containers = containers.Where(c => string.Compare(c.Name, afterName) > 0);

        return await containers
            .OrderBy(c => c.Name)
            .Take(pageSize)
            .Select(x => new ContainerInfo
            {
                Name = x.Name,
                Metadata = (IReadOnlyDictionary<string, string>)x.Metadata
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName)) return null;
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        return await dbContext.ResourceContainers.Where(x => x.Name == resourceContainerName).Select(x => new ContainerInfo
        {
            Name = x.Name,
            Metadata = (IReadOnlyDictionary<string, string>)x.Metadata
        }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName)) return new ResourceContainerUsage(0, 0, 0);
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        return await MeasureContainerAsync(dbContext, resourceContainerName, cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetTopVersionsAsync(string resourceContainerName,
        ResourceVersionOrder order, int count, CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName)) return [];
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        // One query over the container's version rows. Neither order has an index of its own, so it reads them all, as
        // usage does. SQL Server sorts NULL lowest, so a version an early release stored without a length comes last.
        IQueryable<ResourceModel> versions = dbContext.Resources.Where(r => r.ContainerName == resourceContainerName);
        versions = order == ResourceVersionOrder.LargestFirst
            ? versions.OrderByDescending(r => r.ContentLength).ThenBy(r => r.VersionId)
            : versions.Where(r => r.ExpiresAt != null).OrderBy(r => r.ExpiresAt).ThenBy(r => r.VersionId);
        return await versions.Take(count).Select(AsResourceInfo).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<bool> DeleteContainerAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        if (HasNul(resourceContainerName)) return false;
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        if (!await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken).ConfigureAwait(false)) return false;

        // Every version is checked before any goes, so a delete that's refused deletes nothing. A version held or
        // retained after this is left by the batch that reaches it, and the next batch refuses.
        DateTimeOffset now = Now();
        await ThrowIfAnyRetainedAsync(dbContext.Resources.Where(r => r.ContainerName == resourceContainerName), now, cancellationToken).ConfigureAwait(false);

        // Each batch commits in a transaction of its own, so no single transaction holds the whole container's content.
        // A batch takes whole resources, each with its chunks, so a reader never sees part of one, until it reaches the
        // chunk budget. Each pass removes only what earlier passes left, so a delete that is canceled or fails partway
        // is finished by calling it again. A version stored after the last batch takes another round of them, which
        // checks it as it checks the rest.
        bool deleted = false;
        while (true)
        {
            while (await WriteNamesAsync([], DeleteBatchAsync, cancellationToken).ConfigureAwait(false) > 0)
            {
            }
            if (await WriteNamesAsync([], RemoveIfEmptyAsync, cancellationToken).ConfigureAwait(false) is { } removed) return removed;
        }

        async Task<int> DeleteBatchAsync(ResourceDbContext attempt, CancellationToken ct)
        {
            // Staged versions too, so an abandoned upload's chunks go in batches with the rest rather than all at once
            // when the container's row does.
            var candidates = await attempt.Resources.IgnoreQueryFilters()
                .Where(r => r.ContainerName == resourceContainerName)
                .OrderBy(r => r.Name).ThenBy(r => r.VersionId)
                .Select(r => new { r.Id, r.Name, r.VersionId, r.IsLatest, r.StagedAt, r.RetainUntil, r.LegalHold, Chunks = r.Chunks!.Count })
                .Take(MaxResourcesPerDeleteBatch)
                .ToListAsync(ct).ConfigureAwait(false);
            if (candidates.Count == 0) return 0;
            // A staged version's retention applies only once its upload completes.
            if (candidates.FirstOrDefault(c => c.StagedAt is null && (c.LegalHold || c.RetainUntil > now)) is { } retained)
            {
                throw ResourceRetainedException.Create(new ResourceAddress(resourceContainerName, retained.Name), retained.VersionId!,
                    retained.RetainUntil, retained.LegalHold, now);
            }

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
                if (candidate.Chunks > ChunksDeletedPerStatement && attempt.Database.IsRelational()
                    && !await LockVersionInContainerAsync(attempt, candidate.Id, resourceContainerName, now, ct).ConfigureAwait(false))
                    continue;
                deleting.Add((candidate.Id, candidate.Chunks));
            }

            await DeleteLargeVersionsChunksAsync(attempt, deleting, ct).ConfigureAwait(false);
            if (attempt.Database.IsRelational())
            {
                await DeleteVersionsInContainerAsync(attempt, resourceContainerName, batch, now, ct).ConfigureAwait(false);
            }
            else
            {
                // The in-memory provider the unit tests use runs no SQL.
                attempt.Resources.RemoveRange(
                    await attempt.Resources.IgnoreQueryFilters().Where(r => batch.Contains(r.Id)
                        && (r.StagedAt != null || (!r.LegalHold && (r.RetainUntil == null || r.RetainUntil <= now)))).ToListAsync(ct).ConfigureAwait(false));
                await attempt.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            return batch.Count;
        }

        // Removes the container's row, unless a version was stored since the last batch: the row's cascade would delete
        // it unchecked, so this returns null for another round of batches.
        async Task<bool?> RemoveIfEmptyAsync(ResourceDbContext attempt, CancellationToken ct)
        {
            ResourceContainerModel? container = await attempt.ResourceContainers.FirstOrDefaultAsync(x => x.Name == resourceContainerName, ct).ConfigureAwait(false);
            // A retry after a commit whose acknowledgment was lost finds the container gone.
            if (container is null) return deleted;
            if (attempt.Database.IsRelational()
                ? !await LockEmptyContainerAsync(attempt, resourceContainerName, ct).ConfigureAwait(false)
                : await attempt.Resources.IgnoreQueryFilters().AnyAsync(r => r.ContainerName == resourceContainerName, ct).ConfigureAwait(false))
            {
                return null;
            }

            attempt.ResourceContainers.Remove(container);
            await attempt.SaveChangesAsync(ct).ConfigureAwait(false);
            return deleted = true;
        }
    }

    /// <summary>
    /// Throws <see cref="ResourceRetainedException"/> if any of <paramref name="versions"/> is held or retained at
    /// <paramref name="now"/>. Staged versions aren't among them: the staged filter leaves them out, since an upload's
    /// retention applies only once it's complete.
    /// </summary>
    private static async Task ThrowIfAnyRetainedAsync(IQueryable<ResourceModel> versions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Each condition alone, so each seeks on the filtered index of the versions it applies to, in one query.
        ResourceModel? retained = await versions.Where(r => r.LegalHold).Concat(versions.Where(r => r.RetainUntil > now))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (retained is not null) ThrowIfRetained(retained, now);
    }

    /// <summary>
    /// A command on the context's connection, with the command timeout its options give: a command made directly keeps
    /// the driver's default of 30 seconds.
    /// </summary>
    internal static DbCommand NewCommand(ResourceDbContext dbContext)
    {
        DbCommand command = dbContext.Database.GetDbConnection().CreateCommand();
        if (dbContext.Database.GetCommandTimeout() is { } timeout) command.CommandTimeout = timeout;
        return command;
    }

    private Task<ResourceModel?> ResolveAsync(IQueryable<ResourceModel> query,
        string resourceContainerName, string resourceName, string? versionId, CancellationToken cancellationToken) =>
        Version(query, resourceContainerName, resourceName, versionId).FirstOrDefaultAsync(cancellationToken);

    /// <summary>As <see cref="ResolveAsync"/>, with how many chunks the version has, for deleting it.</summary>
    private async Task<(ResourceModel Version, int Chunks)?> ResolveWithChunksAsync(IQueryable<ResourceModel> query,
        string resourceContainerName, string resourceName, string? versionId, CancellationToken cancellationToken) =>
        await Version(query, resourceContainerName, resourceName, versionId)
            .Select(r => new { Version = r, Chunks = r.Chunks!.Count })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) is { } found ? (found.Version, found.Chunks) : null;

    /// <summary>Every version of a name.</summary>
    private IQueryable<ResourceModel> Versions(IQueryable<ResourceModel> query, string resourceContainerName, string resourceName) =>
        HasNul(resourceContainerName) || HasNul(resourceName)
            ? query.Where(r => false)
            : query.Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName);

    /// <summary>The named version, or the current one when <paramref name="versionId"/> is null.</summary>
    private IQueryable<ResourceModel> Version(IQueryable<ResourceModel> query, string resourceContainerName, string resourceName, string? versionId)
    {
        if (HasNul(versionId)) return query.Where(r => false);
        query = Versions(query, resourceContainerName, resourceName);

        // A current version is never staged or in the bin, so the filter on those goes. With its condition, SQL Server
        // was measured caching a scan for this query in some runs, rather than a seek.
        return versionId == null
            ? query.IgnoreQueryFilters().Where(r => r.IsLatest)
            : query.Where(r => r.VersionId == versionId);
    }

    /// <summary>
    /// Whether <paramref name="value"/> has U+0000 and the database can't store it, so no stored name or version ID has
    /// one. A lookup of such a value finds nothing without asking the server, which would refuse it with an error.
    /// </summary>
    private bool HasNul(string? value) => DatabaseRefusingNul is not null && value?.Contains('\0') == true;

    /// <summary>
    /// The name a listing continues after, cut at its first U+0000 when the database can't store one. No stored name has
    /// one then, and it sorts before every other character, so the names after the cut are exactly the names after the
    /// whole.
    /// </summary>
    private string? CutAtNul(string? afterName) =>
        HasNul(afterName) ? afterName![..afterName!.IndexOf('\0')] : afterName;

    /// <summary>
    /// A stored version's ID, hash and length, read in one statement, so the length belongs to the version found even if
    /// it's deleted right after. When the database may hold unmeasured versions, every version's length is added up from
    /// its chunks.
    /// </summary>
    private async Task<StoredVersion?> StoredVersionAsync(IQueryable<ResourceModel> version, CancellationToken cancellationToken) =>
        MayHoldUnmeasuredVersions
            ? await version.Select(r => new StoredVersion(r.Id, r.ContentHash, r.Chunks!.Sum(c => (long?)c.Data.Length) ?? 0))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await version.Select(r => new StoredVersion(r.Id, r.ContentHash, r.ContentLength!.Value))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    private sealed record StoredVersion(long Id, string? ContentHash, long Length);

    // What each database does its own way.

    /// <summary>
    /// How many chunks a staged version has, locking its row, so an upload can't make it current or move its stamp
    /// forward before it goes; none when it's no longer staged, or, given <paramref name="staleBefore"/>, was stamped since.
    /// </summary>
    protected abstract Task<List<int>> LockStagedVersionAsync(ResourceDbContext attempt, long resourceId, DateTimeOffset? staleBefore,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a session's length from <paramref name="expected"/> to <paramref name="after"/>, and its stamp to
    /// <paramref name="now"/>, if it's still staged with that length.
    /// </summary>
    /// <returns>How many rows changed: 1, or 0 when the session isn't as expected.</returns>
    protected abstract Task<int> MoveSessionLengthAsync(ResourceDbContext attempt, long resourceId, long expected, long after,
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>A session's length, or <see langword="null"/> once it's no longer staged.</summary>
    protected abstract Task<long?> SessionLengthAsync(ResourceDbContext attempt, long resourceId, CancellationToken cancellationToken);

    /// <summary>A session's staged version, locking its row, so an append can't store a batch the completion doesn't cover.</summary>
    protected abstract Task<SessionRow?> LockSessionAsync(ResourceDbContext attempt, ResourceAddress resource, string versionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds the batch's chunks to <paramref name="command"/> as parameters, and returns the statement that inserts them;
    /// given <paramref name="stampedAt"/>, it also moves their version's <see cref="ResourceModel.StagedAt"/> to then.
    /// </summary>
    protected abstract string InsertChunks(DbCommand command, List<ResourceChunkModel> batch, DateTimeOffset? stampedAt);

    /// <summary>Copies a version's chunks to another version, in the database.</summary>
    protected abstract Task CopyChunksAsync(ResourceDbContext attempt, long fromId, long toId, CancellationToken cancellationToken);

    /// <summary>The chunks of a version that the range from <paramref name="start"/> to <paramref name="end"/> overlaps.</summary>
    protected abstract Task<ChunkSpan> ChunkSpanAsync(ResourceDbContext dbContext, long resourceId, long start, long end,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds the parameters for reading a page of a version's chunks to <paramref name="command"/>, and returns the
    /// statement: up to <see cref="ChunksPerReadPage"/> chunks after <paramref name="lastIndex"/> (from the first, when
    /// null) through <paramref name="throughIndex"/> (to the last, when null), in index order, each as its index, its
    /// data's length and its data.
    /// </summary>
    protected abstract string ReadChunks(DbCommand command, long resourceId, int? lastIndex, int? throughIndex);

    /// <summary>Deletes up to <see cref="ChunksDeletedPerStatement"/> of a version's chunks.</summary>
    /// <returns>How many it deleted.</returns>
    protected abstract Task<int> DeleteSomeChunksAsync(ResourceDbContext dbContext, long resourceId, CancellationToken cancellationToken);

    /// <summary>A container's usage, from its stored versions.</summary>
    protected abstract Task<ResourceContainerUsage> MeasureContainerAsync(ResourceDbContext dbContext, string resourceContainerName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Locks a version's row, provided it's still in the container and, unless it's staged, neither held nor retained at
    /// <paramref name="now"/>.
    /// </summary>
    /// <returns>Whether it is.</returns>
    protected abstract Task<bool> LockVersionInContainerAsync(ResourceDbContext attempt, long resourceId, string resourceContainerName,
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the container until the transaction ends, so nothing can be stored in it, and says whether it holds no
    /// versions, staged ones included, counting one whose write was still under way.
    /// </summary>
    protected abstract Task<bool> LockEmptyContainerAsync(ResourceDbContext attempt, string resourceContainerName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the versions still in the container, with their chunks, deciding in the delete itself: one moved to
    /// another container since it was listed is left there, and one that's become held or retained at
    /// <paramref name="now"/> since then is left where it is, unless it's staged.
    /// </summary>
    protected abstract Task DeleteVersionsInContainerAsync(ResourceDbContext attempt, string resourceContainerName, List<long> ids,
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Locks each name for the rest of the transaction, in the order given, in one round trip. One lock per name, rather
    /// than locks on its rows, so writers can't deadlock by taking locks in different orders.
    /// </summary>
    protected abstract Task LockNamesAsync(ResourceDbContext dbContext, IReadOnlyList<ResourceAddress> names,
        CancellationToken cancellationToken);

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
