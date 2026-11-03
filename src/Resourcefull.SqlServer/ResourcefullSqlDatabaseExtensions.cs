using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.SqlServer;

/// <summary>
/// Provisions and upgrades the SQL Server database that holds Resourcefull's tables.
/// </summary>
public static class ResourcefullSqlDatabaseExtensions
{
    /// <summary>
    /// Brings the database in the connection string given to <c>UseSqlServer</c> up to date, creating it if it doesn't
    /// exist, and creating the schema given to <c>UseSqlServer</c> if there is one. It applies only the
    /// migrations the database is missing, the same ones the packaged scripts apply, so running it again does nothing.
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
            ResourceDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContextDisposal = dbContext.ConfigureAwait(false);
            activity?.SetTag("resourcefull.schema", dbContext.Schema);
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqlTelemetry.Failed(activity, exception))
        {
            throw;
        }
    }

    /// <summary>
    /// Returns the SQL script that brings a database up to date, with Resourcefull's tables in the schema given to
    /// <c>UseSqlServer</c>, so a DBA can run what <see cref="MigrateResourcefullDatabaseAsync"/> would. Like the
    /// packaged scripts, it applies only the migrations the database is missing, so running it again does nothing.
    /// Generating it reads only the migrations and doesn't connect to the database.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's services, with Resourcefull added and <c>UseSqlServer</c> configured.</param>
    /// <returns>The script, in batches separated by <c>GO</c>, for <c>sqlcmd</c> or SQL Server Management Studio.</returns>
    /// <exception cref="InvalidOperationException">Resourcefull isn't configured with <c>UseSqlServer</c>.</exception>
    public static string GenerateResourcefullScript(this IServiceProvider services)
    {
        var dbContextFactory = services.GetService<IDbContextFactory<ResourceDbContext>>()
            ?? throw new InvalidOperationException($"{nameof(GenerateResourcefullScript)} needs Resourcefull configured with {nameof(ResourcefullSqlOptionsExtensions.UseSqlServer)}.");
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        // As in the packaged scripts: a failed statement rolls back its migration's transaction.
        return "SET XACT_ABORT ON;" + Environment.NewLine + "GO" + Environment.NewLine + Environment.NewLine
            + dbContext.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    }
}
