using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Resourcefull.Tests.Unit;

public class MigrationsTests
{
    [Theory]
    [InlineData("Resourcefull.SqlServer")]
    [InlineData("Resourcefull.PostgreSql")]
    [InlineData("Resourcefull.Sqlite")]
    public void Migrations_ShouldNotBePublic_WhenGivenAnEntityFrameworkProvider(string assemblyName)
    {
        // EF Core generates each migration as a public class, which would make it public API until the next major release.
        IEnumerable<string> exported = Assembly.Load(assemblyName).GetExportedTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) || typeof(ModelSnapshot).IsAssignableFrom(t)).Select(t => t.Name);

        Assert.Empty(exported);
    }
}
