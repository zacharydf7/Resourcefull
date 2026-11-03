using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace Resourcefull.EntityFramework;

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
