using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.Utilities;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Integration")]

namespace Resourcefull.SqlServer;

internal class ResourceSqlRepository(IDbContextFactory<ResourceDbContext> dbContextFactory) : IResourceRepository
{
    private const int SqlForeignKeyViolation = 547;

    public async Task AddResourceAsync(string resourceContainerName, ResourceInfo resourceInfo, Stream stream, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        List<ResourceModel> priorLatest = await dbContext.Resources
            .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceInfo.Name && r.IsLatest)
            .ToListAsync(cancellationToken);
        foreach (ResourceModel prior in priorLatest) prior.IsLatest = false;

        var resource = new ResourceModel
        {
            ContainerName = resourceContainerName,
            Name = resourceInfo.Name,
            VersionId = resourceInfo.VersionId,
            LastModified = resourceInfo.LastModified,
            IsLatest = true,
            ContentType = resourceInfo.ContentType,
            ContentLength = resourceInfo.ContentLength,
            Uri = resourceInfo.Uri,
            Metadata = resourceInfo.Metadata,
            Chunks = await ResourceChunker.ChunksAsync(stream, cancellationToken)
        };

        await dbContext.Resources.AddAsync(resource, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: SqlForeignKeyViolation })
        {
            // Only a missing container is ours to report; any other foreign key stays a database error.
            if (await dbContext.ResourceContainers.AnyAsync(x => x.Name == resourceContainerName, cancellationToken)) throw;

            throw new ResourceContainerNotFoundException($"Resource Container with the name {resourceContainerName} does not exist.", exception);
        }
    }

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
            .OrderByDescending(r => r.LastModified)
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

    public async Task<bool> UpdateResourceAsync(string resourceContainerName, string resourceName, ResourceInfo resourceInfo,
        Stream stream, string? versionId = null, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        ResourceModel? existingResource = await ResolveAsync(
            dbContext.Resources.Include(r => r.Chunks), resourceContainerName, resourceName, versionId, cancellationToken);

        if (existingResource == null) return false;

        if (stream.CanSeek) stream.Position = 0;

        List<ResourceChunkModel>? newChunks = null;
        if (stream.Position != stream.Length)
        {
            var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;
            newChunks = await ResourceChunker.ChunksAsync(memoryStream, cancellationToken);
        }

        dbContext.Resources.Remove(existingResource);

        dbContext.Resources.Add(new ResourceModel
        {
            ContainerName = resourceContainerName,
            Name = resourceInfo.Name,
            VersionId = resourceInfo.VersionId,
            LastModified = resourceInfo.LastModified,
            IsLatest = existingResource.IsLatest,
            ContentLength = resourceInfo.ContentLength,
            ContentType = resourceInfo.ContentType,
            Metadata = resourceInfo.Metadata,
            Uri = resourceInfo.Uri,
            Chunks = newChunks ?? existingResource.Chunks?
                .Select(c => new ResourceChunkModel { Index = c.Index, Data = c.Data })
                .ToList()
        });

        return await dbContext.SaveChangesAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteResourceAsync(string resourceContainerName, string resourceName, string? versionId = null, CancellationToken cancellationToken = default)
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();

        ResourceModel? existingResource = await ResolveAsync(dbContext.Resources, resourceContainerName, resourceName, versionId, cancellationToken);

        if (existingResource == null) return false;

        dbContext.Resources.Remove(existingResource);

        if (existingResource.IsLatest)
        {
            ResourceModel? next = await dbContext.Resources
                .Where(r => r.ContainerName == resourceContainerName && r.Name == resourceName && r.Id != existingResource.Id)
                .OrderByDescending(r => r.LastModified)
                .ThenByDescending(r => r.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (next != null) next.IsLatest = true;
        }

        return await dbContext.SaveChangesAsync(cancellationToken) > 0;
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
        await dbContext.SaveChangesAsync(cancellationToken);
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

        const int batchSize = 100;
        List<ResourceModel> batch;
        do
        {
            batch = await dbContext.Resources
                .Where(r => r.ContainerName == resourceContainerName)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count > 0)
            {
                dbContext.Resources.RemoveRange(batch);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        while (batch.Count == batchSize);

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
