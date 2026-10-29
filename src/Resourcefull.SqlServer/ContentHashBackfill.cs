using Microsoft.EntityFrameworkCore;

namespace Resourcefull.SqlServer;

/// <summary>
/// Records the content hash of versions stored before hashes were kept, and reports every version whose recorded
/// length doesn't match its content. It reads the content of each version without a hash once, so run it when the
/// load allows. It works through those versions in the order they were stored, recording each hash as soon as it is
/// computed, so it can be stopped at any point and run again later to carry on; once every version has a hash, a run
/// reads no content.
/// </summary>
/// <remarks>Registered by <c>UseSqlServer</c>; resolve it from a scope.</remarks>
public sealed class ContentHashBackfill
{
    private const int VersionsPerQuery = 100;

    private readonly IDbContextFactory<ResourceDbContext> _dbContextFactory;

    internal ContentHashBackfill(IDbContextFactory<ResourceDbContext> dbContextFactory) => _dbContextFactory = dbContextFactory;

    /// <summary>Hashes every version that has no hash yet, then checks every version's recorded length.</summary>
    /// <param name="cancellationToken">Stops the run; the hashes already recorded are kept.</param>
    /// <returns>How many hashes this run recorded, and every version whose recorded length is wrong.</returns>
    public async Task<ContentHashBackfillResult> RunAsync(CancellationToken cancellationToken = default)
    {
        int hashed = 0;
        long lastId = 0;

        while (true)
        {
            using ResourceDbContext dbContext = _dbContextFactory.CreateDbContext();
            var versions = await dbContext.Resources
                .Where(r => r.ContentHash == null && r.Id > lastId)
                .OrderBy(r => r.Id)
                .Select(r => new { r.Id, r.LastModified, StoredLength = r.Chunks!.Sum(c => (long?)c.Data.Length) ?? 0 })
                .Take(VersionsPerQuery)
                .ToListAsync(cancellationToken);
            if (versions.Count == 0) break;

            foreach (var version in versions)
            {
                lastId = version.Id;

                string contentHash;
                try
                {
                    using var content = new HashingStream(new ResourceStream(ResourceSqlRepository.PageReader(dbContext, version.Id), version.StoredLength));
                    await content.CopyToAsync(Stream.Null, cancellationToken);
                    contentHash = content.Complete(declaredLength: null, version.LastModified).ContentHash;
                }
                catch (IOException)
                {
                    // Deleted while it was read: there is nothing left to record a hash for.
                    continue;
                }

                hashed += await dbContext.Resources
                    .Where(r => r.Id == version.Id && r.ContentHash == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ContentHash, contentHash), cancellationToken);
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
                .ToListAsync(cancellationToken);

            return new ContentHashBackfillResult(hashed,
                [.. mismatches.Select(m => new ContentLengthMismatch(m.ContainerName, m.Name, m.VersionId!, m.ContentLength!.Value, m.StoredLength))]);
        }
    }
}

/// <summary>What a <see cref="ContentHashBackfill"/> run did.</summary>
/// <param name="Hashed">How many versions this run recorded a hash for.</param>
/// <param name="Mismatches">
/// Every version whose recorded <see cref="ResourceInfo.ContentLength"/> differs from its stored content. A version's
/// hash is recorded either way; its recorded length is left for you to correct, and every run reports it until then.
/// </param>
public sealed record ContentHashBackfillResult(int Hashed, IReadOnlyList<ContentLengthMismatch> Mismatches);

/// <summary>A version whose recorded length differs from the length of its stored content.</summary>
public sealed record ContentLengthMismatch(string ContainerName, string Name, string VersionId, long RecordedLength, long StoredLength);
