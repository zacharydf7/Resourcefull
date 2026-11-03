using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Resourcefull.PostgreSql.Models;

namespace Resourcefull.PostgreSql.ValueGenerators;

internal class VersionIdValueGenerator : ValueGenerator<string>
{
    public override bool GeneratesTemporaryValues => false;

    public override string Next(EntityEntry entry)
    {
        if (entry.Entity is ResourceModel resource) 
        {
            return resource.VersionId ?? Guid.CreateVersion7().ToString();
        }
        return "";
    }
}
