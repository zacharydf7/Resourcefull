using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Resourcefull.Providers;

namespace Resourcefull.Sqlite;

/// <summary>
/// A store's SQLite database: its migrations, applied by <c>MigrateResourcefullDatabaseAsync</c> and written out by
/// <c>GenerateResourcefullScript</c>.
/// </summary>
internal sealed class SqliteDatabase(IDbContextFactory<ResourceDbContext> dbContextFactory, string? storeName) : ResourceDatabase
{
    public override async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = SqliteTelemetry.Start("migrate", storeName);
        try
        {
            ResourceDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContextDisposal = dbContext.ConfigureAwait(false);
            // Write-ahead logging, so reads don't wait for the writer, nor it for them. The database file keeps it once
            // it's set, and it can't be set inside the migrations' transaction.
            await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SqliteTelemetry.Failed(activity, exception))
        {
            throw;
        }
    }

    /// <summary>
    /// Every migration from the first, for a new database: for SQLite, EF Core can't write a script that skips the
    /// migrations a database already has.
    /// </summary>
    public override string GenerateScript()
    {
        using ResourceDbContext dbContext = dbContextFactory.CreateDbContext();
        return dbContext.GetService<IMigrator>().GenerateScript();
    }
}
