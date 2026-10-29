using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.SqlServer;

/// <summary>
/// Provisions and upgrades the SQL Server database that holds Resourcefull's tables.
/// </summary>
public static class ResourcefullSqlDatabaseExtensions
{
    /// <summary>
    /// Brings the database in the connection string given to <c>UseSqlServer</c> to the current schema, creating it if
    /// it doesn't exist. It applies only the migrations the database is missing, the same ones the packaged scripts
    /// apply, so running it again does nothing.
    /// </summary>
    /// <remarks>
    /// Run it from a deployment step, or at the startup of a single instance: enabling <c>READ_COMMITTED_SNAPSHOT</c>,
    /// which one migration does on a database that doesn't have it yet, disconnects every other session on the
    /// database.
    /// </remarks>
    /// <param name="services">The application's services, with Resourcefull added and <c>UseSqlServer</c> configured.</param>
    /// <param name="cancellationToken">Cancels the migration; a migration that was already applying rolls back.</param>
    /// <exception cref="InvalidOperationException">Resourcefull isn't configured with <c>UseSqlServer</c>.</exception>
    public static async Task MigrateResourcefullDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var dbContextFactory = services.GetService<IDbContextFactory<ResourceDbContext>>()
            ?? throw new InvalidOperationException($"{nameof(MigrateResourcefullDatabaseAsync)} needs Resourcefull configured with {nameof(ResourcefullSqlOptionsExtensions.UseSqlServer)}.");
        using Activity? activity = SqlTelemetry.Start("migrate");
        try
        {
            await using ResourceDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception exception) when (SqlTelemetry.Failed(activity, exception))
        {
            throw;
        }
    }
}
