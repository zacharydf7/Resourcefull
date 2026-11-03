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
    public static Task MigrateResourcefullDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
        => MigrateAsync(services, null, cancellationToken);

    /// <summary>
    /// Brings the database of the store named <paramref name="storeName"/> up to date, as
    /// <see cref="MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/> does for the store without a
    /// name. Migrate every store on SQL Server, including stores that share a database with a schema of their own.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's services, with the store added and <c>UseSqlServer</c> configured for it.</param>
    /// <param name="storeName">The name the store was added with, by <c>AddKeyedResourcefull</c>.</param>
    /// <param name="cancellationToken">Cancels the migration; a migration that was already applying rolls back.</param>
    /// <exception cref="ArgumentException"><paramref name="storeName"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">The store isn't configured with <c>UseSqlServer</c>.</exception>
    public static Task MigrateResourcefullDatabaseAsync(this IServiceProvider services, string storeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        return MigrateAsync(services, storeName, cancellationToken);
    }

    private static async Task MigrateAsync(IServiceProvider services, string? storeName, CancellationToken cancellationToken)
    {
        IDbContextFactory<ResourceDbContext> dbContextFactory =
            DbContextFactoryOf(services, storeName, nameof(MigrateResourcefullDatabaseAsync));
        using Activity? activity = SqlTelemetry.Start("migrate", storeName);
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
    /// <c>UseSqlServer</c>, so a DBA can run what
    /// <see cref="MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/> would. Like the packaged
    /// scripts, it applies only the migrations the database is missing, so running it again does nothing.
    /// Generating it reads only the migrations and doesn't connect to the database.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's services, with Resourcefull added and <c>UseSqlServer</c> configured.</param>
    /// <returns>The script, in batches separated by <c>GO</c>, for <c>sqlcmd</c> or SQL Server Management Studio.</returns>
    /// <exception cref="InvalidOperationException">Resourcefull isn't configured with <c>UseSqlServer</c>.</exception>
    public static string GenerateResourcefullScript(this IServiceProvider services) => GenerateScript(services, null);

    /// <summary>
    /// Returns the SQL script for the database of the store named <paramref name="storeName"/>, as
    /// <see cref="GenerateResourcefullScript(IServiceProvider)"/> does for the store without a name.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's services, with the store added and <c>UseSqlServer</c> configured for it.</param>
    /// <param name="storeName">The name the store was added with, by <c>AddKeyedResourcefull</c>.</param>
    /// <returns>The script, in batches separated by <c>GO</c>, for <c>sqlcmd</c> or SQL Server Management Studio.</returns>
    /// <exception cref="ArgumentException"><paramref name="storeName"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">The store isn't configured with <c>UseSqlServer</c>.</exception>
    public static string GenerateResourcefullScript(this IServiceProvider services, string storeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        return GenerateScript(services, storeName);
    }

    private static string GenerateScript(IServiceProvider services, string? storeName)
    {
        using ResourceDbContext dbContext = DbContextFactoryOf(services, storeName, nameof(GenerateResourcefullScript)).CreateDbContext();
        // As in the packaged scripts: a failed statement rolls back its migration's transaction.
        return "SET XACT_ABORT ON;" + Environment.NewLine + "GO" + Environment.NewLine + Environment.NewLine
            + dbContext.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    }

    /// <summary>The store's context factory, or an exception naming <paramref name="caller"/> if it isn't on SQL Server.</summary>
    internal static IDbContextFactory<ResourceDbContext> DbContextFactoryOf(IServiceProvider services, string? storeName,
        string caller) =>
        services.GetStoreService<IDbContextFactory<ResourceDbContext>>(storeName) ?? throw new InvalidOperationException(
            storeName is null
                ? $"{caller} needs Resourcefull configured with {nameof(ResourcefullSqlOptionsExtensions.UseSqlServer)}."
                : $"{caller} needs a store named '{storeName}' configured with {nameof(ResourcefullSqlOptionsExtensions.UseSqlServer)}.");
}
