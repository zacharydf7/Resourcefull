using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore;

namespace Resourcefull.PostgreSql;
internal class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ResourceDbContext>
{
    ResourceDbContext IDesignTimeDbContextFactory<ResourceDbContext>.CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ResourceDbContext>();
        builder.UseNpgsql("Host=localhost");
        return new ResourceDbContext(builder.Options);
    }
}