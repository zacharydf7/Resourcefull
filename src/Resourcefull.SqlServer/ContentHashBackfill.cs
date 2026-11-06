using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Resourcefull.Providers;

namespace Resourcefull.SqlServer;

/// <summary>
/// Records the content hash of versions stored before hashes were kept, and reports every version whose recorded
/// length doesn't match its content. It reads the content of each version without a hash once, so run it when the
/// load allows. It works through those versions in the order they were stored, recording each hash as soon as it is
/// computed, so it can be stopped at any point and run again later to carry on; once every version has a hash, a run
/// reads no content.
/// </summary>
/// <remarks>
/// Registered by <c>UseSqlServer</c>; resolve it from a scope, by the store's name for a store added with
/// <c>AddKeyedResourcefull</c>.
/// </remarks>
public sealed class ContentHashBackfill
{
    private const int VersionsPerQuery = 100;

    private readonly IDbContextFactory<ResourceDbContext> _dbContextFactory;

    /// <summary>The repository, whose page reader reads a version's chunks.</summary>
    private readonly ResourceSqlRepository _repository;

    private readonly string? _storeName;

    internal ContentHashBackfill(IDbContextFactory<ResourceDbContext> dbContextFactory, string? storeName = null)
    {
        _dbContextFactory = dbContextFactory;
        _repository = new ResourceSqlRepository(dbContextFactory);
        _storeName = storeName;
    }

    /// <summary>Hashes every version that has no hash yet, then checks every version's recorded length.</summary>
    /// <param name="cancellationToken">Stops the run; the hashes already recorded are kept.</param>
    /// <returns>How many hashes this run recorded, and every version whose recorded length is wrong.</returns>
    public async Task<ContentHashBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = SqlTelemetry.Start("backfill", _storeName);
        try
        {
            ContentHashBackfillResult result = await RunCoreAsync(cancellationToken).ConfigureAwait(false);
            activity?.SetTag("resourcefull.hashed", result.Hashed);
            activity?.SetTag("resourcefull.mismatches", result.Mismatches.Count);
            return result;
        }
        catch (Exception exception) when (SqlTelemetry.Failed(activity, exception))
        {
            throw;
        }
    }

    private async Task<ContentHashBackfillResult> RunCoreAsync(CancellationToken cancellationToken)
    {
        long hashed = 0;
        long lastId = 0;

        while (true)
        {
            using ResourceDbContext dbContext = _dbContextFactory.CreateDbContext();
            var versions = await dbContext.Resources
                .Where(r => r.ContentHash == null && r.Id > lastId)
                .OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.LastModified, StoredLength = r.Chunks!.Sum(c => (long?)c.Data.Length) ?? 0 })
                .Take(VersionsPerQuery)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (versions.Count == 0) break;

            foreach (var version in versions)
            {
                lastId = version.Id;

                string contentHash;
                try
                {
                    using var content = new HashingStream(new ResourceStream(_repository.PageReader(dbContext, version.Id), version.StoredLength));
                    await content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                    contentHash = content.Complete(declaredLength: null, version.LastModified).ContentHash;
                }
                catch (IOException)
                {
                    // Deleted while it was read: there is nothing left to record a hash for.
                    continue;
                }

                hashed += await dbContext.Resources
                    .Where(r => r.Id == version.Id && r.ContentHash == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ContentHash, contentHash), cancellationToken).ConfigureAwait(false);
            }
        }

        // Checked apart from the hashing, from what is stored now, so every run reports every wrong length until it
        // is corrected, whatever happened to earlier runs.
        using (ResourceDbContext dbContext = _dbContextFactory.CreateDbContext())
        {
            var mismatches = await dbContext.Resources
                .Select(r => new
                {
                    r.ContainerName, r.Name, r.VersionId, r.ContentLength,
                    StoredLength = r.Chunks!.Sum(c => (long?)c.Data.Length) ?? 0
                })
                .Where(r => r.ContentLength != null && r.ContentLength != r.StoredLength)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            return new ContentHashBackfillResult
            {
                Hashed = hashed,
                Mismatches = [.. mismatches.Select(m => new ContentLengthMismatch
                {
                    ContainerName = m.ContainerName,
                    Name = m.Name,
                    VersionId = m.VersionId!,
                    RecordedLength = m.ContentLength!.Value,
                    StoredLength = m.StoredLength
                })]
            };
        }
    }
}

/// <summary>What a <see cref="ContentHashBackfill"/> run did.</summary>
public sealed record ContentHashBackfillResult
{
    /// <summary>How many versions this run recorded a hash for.</summary>
    public required long Hashed { get; init; }

    /// <summary>
    /// Every version whose recorded <see cref="ResourceInfo.ContentLength"/> differs from its stored content. A version's
    /// hash is recorded either way; its recorded length is left for you to correct, and every run reports it until then.
    /// </summary>
    public required IReadOnlyList<ContentLengthMismatch> Mismatches { get; init; }
}

/// <summary>A version whose recorded length differs from the length of its stored content.</summary>
public sealed record ContentLengthMismatch
{
    /// <summary>The name of the container the version is in.</summary>
    public required string ContainerName { get; init; }

    /// <summary>The resource's name.</summary>
    public required string Name { get; init; }

    /// <summary>The version's ID.</summary>
    public required string VersionId { get; init; }

    /// <summary>The length recorded for the version, its <see cref="ResourceInfo.ContentLength"/>.</summary>
    public required long RecordedLength { get; init; }

    /// <summary>The length of the version's stored content.</summary>
    public required long StoredLength { get; init; }
}
