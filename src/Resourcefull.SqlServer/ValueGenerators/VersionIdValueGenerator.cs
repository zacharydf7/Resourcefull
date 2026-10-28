using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer.ValueGenerators;

internal class VersionIdValueGenerator : ValueGenerator<string>
{
    public override bool GeneratesTemporaryValues => false;

    public override string Next(EntityEntry entry)
    {
        if (entry.Entity is ResourceModel resource) 
        {
            return resource.VersionId ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        }
        return "";
    }
}
