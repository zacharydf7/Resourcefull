using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.SqlServer;

/// <summary>
/// Configures Resourcefull to store resources in SQL Server.
/// </summary>
public static class ResourcefullSqlOptionsExtensions
{
    /// <summary>
    /// Stores resources in the SQL Server database that <paramref name="connectionString"/> names, retrying transient
    /// failures (including deadlocks). Provision the database with
    /// <see cref="ResourcefullSqlDatabaseExtensions.MigrateResourcefullDatabaseAsync"/> or the packaged scripts.
    /// Also registers <see cref="ContentHashBackfill"/>.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public static ResourcefullOptionsBuilder UseSqlServer(this ResourcefullOptionsBuilder builder, string connectionString)
    {
        builder.UseProvider<ResourceSqlRepository>();

        builder.Services.AddDbContextFactory<ResourceDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
                sql.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: [1205])));
        builder.Services.AddScoped(services => new ContentHashBackfill(services.GetRequiredService<IDbContextFactory<ResourceDbContext>>()));
        return builder;
    }
}