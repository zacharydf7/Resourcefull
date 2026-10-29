using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.Utilities;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Integration")]

namespace Resourcefull.SqlServer;

internal class ResourceSqlRepository(IDbContextFactory<ResourceDbContext> dbContextFactory) : IResourceRepository
{
    private const int SqlForeignKeyViolation = 547;
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;
    private const int MaxResourcesPerDeleteBatch = 100;

    /// <summary>
    /// Caps the content one batch of a container delete removes, counted in chunks, so no single transaction has to log
    /// a whole container. A resource bigger than the cap still goes in a batch of its own.
    /// </summary>
    internal int ChunksPerDeleteBatch { get; init; } = 256;

    public async Task<WrittenContent> AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, bool createOnly = false, CancellationToken cancellationToken = default)
    {
        // The caller's stream can only be read once, so it is chunked, and measured, before the write, which may be
        // retried.
        if (stream.CanSeek) stream.Position = 0;
        using var content = new HashingStream(stream);
        List<ResourceChunkModel> chunks = await ResourceChunker.ChunksAsync(content, cancellationToken);
        WrittenContent written = content.Complete(resourceInfo.ContentLength);

        try
        {
            await WriteNameAsync(resourceContainerName, resourceInfo.Name, async (dbContext, ct) =>
            {
                // A retry after a commit whose acknowledgement was lost finds its own version already stored.
                if (await dbContext.Resources.AnyAsync(r => r.ContainerName == resourceContainerName && r.Name == resourceInfo.Name
                    && r.VersionId == resourceInfo.VersionId, ct))
                    return 0;

                List<ResourceModel> priorLatest = await dbContext.Resources
                    .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceInfo.Name && r.IsLatest)
                    .ToListAsync(ct);
                if (createOnly && priorLatest.Count > 0)
                    throw new ResourceConflictException($"Resource with name {resourceInfo.Name} already exists.");

                // Clear the old current version before inserting the new one, so the unique "latest" index never
                // holds two rows for the name in the middle of the write.
                foreach (ResourceModel prior in priorLatest) prior.IsLatest = false;
                await dbContext.SaveChangesAsync(ct);

                dbContext.Resources.Add(new ResourceModel
                {
                    ContainerName = resourceContainerName,
                    Name = resourceInfo.Name,
                    VersionId = resourceInfo.VersionId,
                    LastModified = resourceInfo.LastModified,
                    IsLatest = true,
                    ContentType = resourceInfo.ContentType,
                    ContentLength = written.ContentLength,
                    Uri = resourceInfo.Uri,
                    Metadata = resourceInfo.Metadata,
                    Chunks = [.. chunks.Select(c => new ResourceChunkModel { Index = c.Index, Data = c.Data })]
                });
                return await dbContext.SaveChangesAsync(ct);
            }, cancellationToken);

            return written;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: SqlForeignKeyViolation })
        {
            // Only a missing container is ours to report; any other foreign key stays a database error.
            using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
            if (await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken)) throw;

            throw new ResourceContainerNotFoundException($"Resource Container with the name {resourceContainerName} does not exist.", exception);
        }
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
            using ResourceDbContext attempt = dbContextFactory.CreateDbContext();
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
            Metadata = existingResource.Metadata,
            Uri = existingResource.Uri
        };
    }

    public async Task<Stream?> GetResourceStreamAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        ResourceModel? existingResource = await ResolveAsync(
            dbContext.Resources.Include(r => r.Chunks), resourceContainerName, resourceName, versionId, cancellationToken);

        return existingResource != null
            ? new ResourceStream([.. existingResource.Chunks!])
            : null;
    }

    public Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default) =>
        // Under the name's lock, so a concurrent delete of the same version finds it gone instead of failing, and the
        // promotion of the next version can't race a write.
        WriteNameAsync(resourceContainerName, resourceName, async (dbContext, ct) =>
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
        }, cancellationToken);

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

    private static async Task<ResourceModel?> ResolveAsync(IQueryable<ResourceModel> query,
        string resourceContainerName, string resourceName, string? versionId, CancellationToken cancellationToken)
    {
        query = query.Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName);

        return versionId == null
            ? await query.FirstOrDefaultAsync(r => r.IsLatest, cancellationToken)
            : await query.FirstOrDefaultAsync(r => r.VersionId == versionId, cancellationToken);
    }
}
