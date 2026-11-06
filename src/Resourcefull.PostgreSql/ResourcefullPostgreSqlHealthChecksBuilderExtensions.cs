using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Resourcefull.PostgreSql;

namespace Resourcefull;

/// <summary>
/// Adds Resourcefull's PostgreSQL health check.
/// </summary>
public static class ResourcefullPostgreSqlHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a check that the PostgreSQL database given to <c>UsePostgreSql</c> can be reached and has every migration
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
    /// <param name="storeName">
    /// The store to check, added with <c>AddKeyedResourcefull</c>; <see langword="null"/> for the store without a name.
    /// Give each store's check a <paramref name="name"/> of its own.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IHealthChecksBuilder AddResourcefullPostgreSqlCheck(this IHealthChecksBuilder builder,
        string name = "resourcefull", HealthStatus? failureStatus = null, IEnumerable<string>? tags = null,
        TimeSpan? timeout = null, string? storeName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            services => new ResourcefullPostgreSqlHealthCheck(
                services.GetStoreService<IDbContextFactory<ResourceDbContext>>(storeName) ?? throw new InvalidOperationException(
                    storeName is null
                        ? $"{nameof(AddResourcefullPostgreSqlCheck)} needs Resourcefull configured with {nameof(ResourcefullPostgreSqlOptionsExtensions.UsePostgreSql)}."
                        : $"{nameof(AddResourcefullPostgreSqlCheck)} needs a store named '{storeName}' configured with {nameof(ResourcefullPostgreSqlOptionsExtensions.UsePostgreSql)}."),
                storeName),
            failureStatus, tags, timeout));
    }
}
