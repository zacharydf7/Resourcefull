using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Resourcefull.SqlServer;

/// <summary>Builds a model for each schema, so two stores with different schemas in one process don't share one.</summary>
internal sealed class SchemaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), (context as ResourceDbContext)?.Schema, designTime);
}
