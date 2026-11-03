using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Resourcefull.Providers;

namespace Resourcefull.PostgreSql;

/// <summary>
/// A store's PostgreSQL database: its migrations, applied by <c>MigrateResourcefullDatabaseAsync</c> and written out
/// by <c>GenerateResourcefullScript</c>, in the schema given to <c>UsePostgreSql</c> if there is one.
/// </summary>
internal sealed class PostgreSqlDatabase(IDbContextFactory<ResourceDbContext> dbContextFactory, string? schema,
    string? storeName) : ResourceDatabase
{
    public override async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = PostgreSqlTelemetry.Start("migrate", storeName);
        try
        {
            activity?.SetTag("resourcefull.schema", schema);
            ResourceDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContextDisposal = dbContext.ConfigureAwait(false);
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (PostgreSqlTelemetry.Failed(activity, exception))
        {
            throw;
        }
    }

    public override string GenerateScript()
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        return dbContext.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
    }
}
