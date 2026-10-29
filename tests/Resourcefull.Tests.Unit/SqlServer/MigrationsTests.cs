using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Resourcefull.SqlServer;

namespace Resourcefull.Tests.Unit.SqlServer;

public class MigrationsTests
{
    [Fact]
    public void Migrations_ShouldNotBePublic_WhenCalled()
    {
        // EF Core generates each migration as a public class, which would make it public API until the next major release.
        IEnumerable<string> exported = typeof(ResourceDbContext).Assembly.GetExportedTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) || typeof(ModelSnapshot).IsAssignableFrom(t)).Select(t => t.Name);

        Assert.Empty(exported);
    }
}
