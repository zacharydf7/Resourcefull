using Microsoft.EntityFrameworkCore;

namespace Resourcefull.PostgreSql;

/// <summary>
/// Makes one store's contexts. <c>AddDbContextFactory</c> registers a single factory for the context type, so each
/// store registers one of these under its name instead.
/// </summary>
internal sealed class ResourceDbContextFactory(DbContextOptions<ResourceDbContext> options) : IDbContextFactory<ResourceDbContext>
{
    public ResourceDbContext CreateDbContext() => new(options);
}
