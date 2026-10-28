using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.SqlServer;

public static class ResourcefullSqlOptionsExtensions
{
    /// <summary>
    /// Configures Resourcefull to connect to a a Microsoft SQL Server database.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="connectionString">Specifies the connection information to connect to a Microsoft SQL Server database</param>
    /// <exception cref="InvalidOperationException"></exception>
    public static ResourcefullOptionsBuilder UseSqlServer(this ResourcefullOptionsBuilder builder, string connectionString)
    {
        builder.UseProvider<ResourceSqlRepository>();

        builder.Services.AddDbContextFactory<ResourceDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: [1205])));
        return builder;
    }
}