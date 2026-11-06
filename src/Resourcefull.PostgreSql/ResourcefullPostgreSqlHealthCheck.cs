using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Resourcefull.PostgreSql;

/// <summary>
/// Checks that PostgreSQL can be reached and that the database has every migration this release needs. Reading the
/// migration history is the reachability check too.
/// </summary>
internal sealed class ResourcefullPostgreSqlHealthCheck(IDbContextFactory<ResourceDbContext> dbContextFactory,
    string? storeName = null) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using Activity? activity = PostgreSqlTelemetry.Start("health_check", storeName);
        try
        {
            ResourceDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var dbContextDisposal = dbContext.ConfigureAwait(false);
            string[] known = [.. dbContext.Database.GetMigrations()];
            string[] applied = [.. await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)];
            string[] pending = [.. known.Except(applied)];
            // The history table is EF Core's, so an application sharing the database keeps its own migrations there
            // too; only migrations named as Resourcefull's are from a newer release.
            string[] unknown = [.. applied.Except(known).Where(ResourceDbContext.IsResourcefullMigration)];
            activity?.SetTag("resourcefull.pending_migrations", pending.Length);

            var data = new Dictionary<string, object>
            {
                ["provider"] = "postgresql",
                ["pendingMigrations"] = pending,
                ["unknownMigrations"] = unknown
            };
            if (pending.Length > 0)
            {
                // This release's queries would fail against the older schema.
                return new HealthCheckResult(context.Registration.FailureStatus, data: data, description:
                    $"The database is missing {pending.Length} migration(s) this release needs: {string.Join(", ", pending)}.");
            }
            if (unknown.Length > 0)
            {
                // A newer release upgraded the schema first, which is a supported rollout: this one still works.
                return HealthCheckResult.Degraded(data: data, description:
                    $"The database has {unknown.Length} migration(s) this release doesn't know, from a newer release: " +
                    string.Join(", ", unknown) + ".");
            }
            return HealthCheckResult.Healthy(
                "PostgreSQL can be reached, and the database has every migration this release needs.", data);
        }
        catch (Exception exception) when (PostgreSqlTelemetry.Failed(activity, exception))
        {
            // Never reached: recorded on the span, a failure or timeout goes to the health check service, which
            // reports it, with the exception, as the registration's failure status.
            throw;
        }
    }
}
