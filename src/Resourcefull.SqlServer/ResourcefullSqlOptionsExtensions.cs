using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        => UseSqlServer(builder, connectionString, _ => { });

    /// <summary>
    /// Stores resources in SQL Server as <see cref="UseSqlServer(ResourcefullOptionsBuilder, string)"/> does, then
    /// applies <paramref name="configure"/> to EF Core's SQL Server options, to change the command timeout or the
    /// retries, for example. Replacing the retry policy, such as with <c>EnableRetryOnFailure</c>, also stops
    /// Resourcefull counting retries in <c>resourcefull.sqlserver.retries</c>; give it error 50001 to retry, which a
    /// write whose lock on its resource name lost a deadlock raises.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <param name="configure">Changes EF Core's SQL Server options, after Resourcefull has set its own.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public static ResourcefullOptionsBuilder UseSqlServer(this ResourcefullOptionsBuilder builder, string connectionString,
        Action<SqlServerDbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        builder.UseProvider<ResourceSqlRepository>();

        builder.Services.AddDbContextFactory<ResourceDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.ExecutionStrategy(dependencies => new RetryCountingExecutionStrategy(dependencies,
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: [1205, ResourceSqlRepository.NameLockNotGrantedError]));
                configure(sql);
            }));
        builder.Services.AddScoped(services => new ContentHashBackfill(services.GetRequiredService<IDbContextFactory<ResourceDbContext>>()));
        return builder;
    }
}