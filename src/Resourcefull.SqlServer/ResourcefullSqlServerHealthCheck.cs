using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Resourcefull.SqlServer;

/// <summary>
/// Checks that SQL Server can be reached and that the database has every migration this release needs. Reading the
/// migration history is the reachability check too.
/// </summary>
internal sealed class ResourcefullSqlServerHealthCheck(IDbContextFactory<ResourceDbContext> dbContextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using Activity? activity = SqlTelemetry.Start("health_check");
        try
        {
            await using ResourceDbContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            string[] known = [.. dbContext.Database.GetMigrations()];
            string[] applied = [.. await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken)];
            string[] pending = [.. known.Except(applied)];
            // The history table is EF Core's, so an application sharing the database keeps its own migrations there
            // too; only migrations named as Resourcefull's are from a newer release.
            string[] unknown = [.. applied.Except(known).Where(ResourceDbContext.IsResourcefullMigration)];
            activity?.SetTag("resourcefull.pending_migrations", pending.Length);

            var data = new Dictionary<string, object>
            {
                ["provider"] = "sqlserver",
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
                "SQL Server can be reached, and the database has every migration this release needs.", data);
        }
        catch (Exception exception) when (SqlTelemetry.Failed(activity, exception))
        {
            // Never reached: recorded on the span, a failure or timeout goes to the health check service, which
            // reports it, with the exception, as the registration's failure status.
            throw;
        }
    }
}

/// <summary>Adds Resourcefull's SQL Server health check. <para>Coming in 3.2.</para></summary>
public static class ResourcefullSqlServerHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a check that the SQL Server database given to <c>UseSqlServer</c> can be reached and has every migration
    /// this release needs. It reports Healthy when both hold; <paramref name="failureStatus"/> when the database
    /// can't be reached or is missing migrations, naming them, since this release's queries would fail against the
    /// older schema; and Degraded when the database has migrations of Resourcefull's that this release doesn't know,
    /// which happens when a newer release upgraded it first. Your own EF Core migrations in the same history are
    /// ignored. It reads only the migration history, so it's safe to run every few seconds.
    /// </summary>
    /// <remarks>
    /// Use it for readiness, not liveness: when the database is down or behind, restarting the application doesn't
    /// help, and a failing liveness check would restart every instance.
    /// </remarks>
    /// <param name="builder">The builder <c>AddHealthChecks()</c> returned.</param>
    /// <param name="name">The check's name in reports.</param>
    /// <param name="failureStatus">What to report for an unreachable or outdated database; Unhealthy when null.</param>
    /// <param name="tags">Tags to filter checks by, such as <c>ready</c>.</param>
    /// <param name="timeout">How long the check may take before it counts as failed; no limit when null.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IHealthChecksBuilder AddResourcefullSqlServerCheck(this IHealthChecksBuilder builder,
        string name = "resourcefull", HealthStatus? failureStatus = null, IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            services => new ResourcefullSqlServerHealthCheck(services.GetService<IDbContextFactory<ResourceDbContext>>()
                ?? throw new InvalidOperationException($"{nameof(AddResourcefullSqlServerCheck)} needs Resourcefull " +
                    $"configured with {nameof(ResourcefullSqlOptionsExtensions.UseSqlServer)}.")),
            failureStatus, tags, timeout));
    }
}
