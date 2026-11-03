using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Resourcefull.Providers;

namespace Resourcefull.SqlServer;

/// <summary>
/// A store's SQL Server database: its migrations, applied by <c>MigrateResourcefullDatabaseAsync</c> and written out
/// by <c>GenerateResourcefullScript</c>, in the schema given to <c>UseSqlServer</c> if there is one.
/// </summary>
internal sealed class SqlServerDatabase(IDbContextFactory<ResourceDbContext> dbContextFactory, string? storeName) : ResourceDatabase
{
    public override async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
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

    public override string GenerateScript()
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        // As in the packaged scripts: a failed statement rolls back its migration's transaction.
        return "SET XACT_ABORT ON;" + Environment.NewLine + "GO" + Environment.NewLine + Environment.NewLine
            + dbContext.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    }
}
