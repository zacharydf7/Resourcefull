using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Resourcefull.Sqlite;

/// <summary>
/// Checks that the SQLite database can be opened and has every migration this release needs. Reading the migration
/// history is the check that it opens, too.
/// </summary>
internal sealed class ResourcefullSqliteHealthCheck(IDbContextFactory<ResourceDbContext> dbContextFactory,
    string? storeName = null) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using Activity? activity = SqliteTelemetry.Start("health_check", storeName);
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
                ["provider"] = "sqlite",
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
                "The SQLite database can be opened, and it has every migration this release needs.", data);
        }
        catch (Exception exception) when (SqliteTelemetry.Failed(activity, exception))
        {
            // Never reached: recorded on the span, a failure or timeout goes to the health check service, which
            // reports it, with the exception, as the registration's failure status.
            throw;
        }
    }
}

/// <summary>
/// Adds Resourcefull's SQLite health check.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullSqliteHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a check that the SQLite database given to <c>UseSqlite</c> can be opened and has every migration
    /// this release needs. It reports Healthy when both hold; <paramref name="failureStatus"/> when the database
    /// can't be opened or is missing migrations, naming them, since this release's queries would fail against the
    /// older schema; and Degraded when the database has migrations of Resourcefull's that this release doesn't know,
    /// which happens when a newer release upgraded it first. Your own EF Core migrations in the same history are
    /// ignored. It reads only the migration history, so it's safe to run every few seconds.
    /// </summary>
    /// <remarks>
    /// Use it for readiness, not liveness: when the database can't be opened or is behind, restarting the application
    /// doesn't help, and a failing liveness check would restart it in a loop.
    /// </remarks>
    /// <param name="builder">The builder <c>AddHealthChecks()</c> returned.</param>
    /// <param name="name">The check's name in reports.</param>
    /// <param name="failureStatus">What to report for a database that can't be opened or is outdated; Unhealthy when null.</param>
    /// <param name="tags">Tags to filter checks by, such as <c>ready</c>.</param>
    /// <param name="timeout">How long the check may take before it counts as failed; no limit when null.</param>
    /// <param name="storeName">
    /// The store to check, added with <c>AddKeyedResourcefull</c>; <see langword="null"/> for the store without a name.
    /// Give each store's check a <paramref name="name"/> of its own.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IHealthChecksBuilder AddResourcefullSqliteCheck(this IHealthChecksBuilder builder,
        string name = "resourcefull", HealthStatus? failureStatus = null, IEnumerable<string>? tags = null,
        TimeSpan? timeout = null, string? storeName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            services => new ResourcefullSqliteHealthCheck(
                services.GetStoreService<IDbContextFactory<ResourceDbContext>>(storeName) ?? throw new InvalidOperationException(
                    storeName is null
                        ? $"{nameof(AddResourcefullSqliteCheck)} needs Resourcefull configured with {nameof(ResourcefullSqliteOptionsExtensions.UseSqlite)}."
                        : $"{nameof(AddResourcefullSqliteCheck)} needs a store named '{storeName}' configured with {nameof(ResourcefullSqliteOptionsExtensions.UseSqlite)}."),
                storeName),
            failureStatus, tags, timeout));
    }
}
