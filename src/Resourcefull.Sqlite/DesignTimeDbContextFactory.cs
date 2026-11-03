using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore;

namespace Resourcefull.Sqlite;
internal class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ResourceDbContext>
{
    ResourceDbContext IDesignTimeDbContextFactory<ResourceDbContext>.CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ResourceDbContext>();
        builder.UseSqlite("Data Source=:memory:");
        return new ResourceDbContext(builder.Options);
    }
}