using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore;

namespace Resourcefull.SqlServer;
internal class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ResourceDbContext>
{
    ResourceDbContext IDesignTimeDbContextFactory<ResourceDbContext>.CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ResourceDbContext>();
        builder.UseSqlServer("");
        return new ResourceDbContext(builder.Options);
    }
}