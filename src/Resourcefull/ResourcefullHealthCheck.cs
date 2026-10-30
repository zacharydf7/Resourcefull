using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Resourcefull;

/// <summary>
/// Checks that the configured provider's storage can be reached, with one read that changes nothing: it looks up a
/// container that needn't exist.
/// </summary>
internal sealed class ResourcefullHealthCheck(IResourceRepository repository, ResourcefullOptions options) : IHealthCheck
{
    internal const string ProbeContainerName = "resourcefull-health-check";

    /// <remarks>
    /// A failed read throws: the health check service reports it, with the exception, as the registration's failure
    /// status, and a timeout the same way.
    /// </remarks>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var telemetry = new ClientTelemetry(Telemetry.ProviderName(repository), options);
        await telemetry.RunAsync("health_check", null, null,
            () => repository.GetResourceContainerInfoAsync(ProbeContainerName, cancellationToken));
        return HealthCheckResult.Healthy($"Resourcefull's storage ({telemetry.Provider}) can be reached.",
            new Dictionary<string, object> { ["provider"] = telemetry.Provider });
    }
}

/// <summary>Adds Resourcefull's health check. <para>Coming in 3.2.</para></summary>
public static class ResourcefullHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a check that Resourcefull's storage can be reached, through whichever provider is configured: one read that
    /// changes nothing, so it's safe to run every few seconds. It reports Healthy when the read succeeds, and
    /// <paramref name="failureStatus"/> with the exception when it fails or times out. With SQL Server, use
    /// <c>AddResourcefullSqlServerCheck</c>, which also checks the schema.
    /// </summary>
    /// <remarks>
    /// Use it for readiness, not liveness: when storage is down, restarting the application doesn't help, and a failing
    /// liveness check would restart every instance.
    /// </remarks>
    /// <param name="builder">The builder <c>AddHealthChecks()</c> returned.</param>
    /// <param name="name">The check's name in reports.</param>
    /// <param name="failureStatus">What to report when storage can't be reached; Unhealthy when null.</param>
    /// <param name="tags">Tags to filter checks by, such as <c>ready</c>.</param>
    /// <param name="timeout">How long the check may take before it counts as failed; no limit when null.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IHealthChecksBuilder AddResourcefullCheck(this IHealthChecksBuilder builder, string name = "resourcefull",
        HealthStatus? failureStatus = null, IEnumerable<string>? tags = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Add(new HealthCheckRegistration(name,
            services => new ResourcefullHealthCheck(
                services.GetService<IResourceRepository>() ?? throw new InvalidOperationException(
                    $"{nameof(AddResourcefullCheck)} needs Resourcefull added with " +
                    $"{nameof(ResourcefullServiceCollectionExtensions.AddResourcefull)}."),
                services.GetRequiredService<IOptions<ResourcefullOptions>>().Value),
            failureStatus, tags, timeout));
    }
}
