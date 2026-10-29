using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Resourcefull;

/// <summary>
/// Configures Resourcefull inside <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/>: its storage
/// provider, versioning and URI provider.
/// </summary>
public class ResourcefullOptionsBuilder
{
    /// <summary>
    /// Gets whether a storage provider has been configured. Exactly one must be, through a provider package's
    /// <c>Use…</c> extension or <see cref="UseProvider{TRepository}"/>.
    /// </summary>
    public bool ProviderConfigured { get; private set; }

    /// <summary>
    /// Gets the application's service collection, for provider extensions to register their services in.
    /// </summary>
    public IServiceCollection Services { get; init; }

    /// <summary>
    /// Creates a builder that registers into <paramref name="services"/>. Applications get one from
    /// <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> rather than creating it.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    public ResourcefullOptionsBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// Uses <typeparamref name="TRepository"/> as the storage provider. Provider packages call this from their own
    /// <c>Use…</c> extension; a custom provider can be registered with it directly.
    /// </summary>
    /// <typeparam name="TRepository">The provider's <see cref="IResourceRepository"/> implementation.</typeparam>
    /// <param name="lifetime">How long one instance serves: singleton for a provider that holds its own state.</param>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public ResourcefullOptionsBuilder UseProvider<TRepository>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TRepository : class, IResourceRepository
    {
        if (ProviderConfigured)
        {
            throw new InvalidOperationException("Provider has already been configured.");
        }

        Services.RemoveAll<IResourceRepository>();
        Services.Add(new ServiceDescriptor(typeof(IResourceRepository), typeof(TRepository), lifetime));
        ProviderConfigured = true;
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> to give containers and resources their <c>Uri</c>, replacing any URI provider set
    /// before. It is registered as a scoped service, so it can depend on other services.
    /// </summary>
    /// <typeparam name="T">The <see cref="IUriProvider"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder SetUriProvider<T>() where T : class, IUriProvider
    {
        ServiceDescriptor? descriptor = Services.SingleOrDefault(
                d => d.ServiceType ==
                    typeof(IUriProvider));

        if (descriptor != null)
        {
            Services.Remove(descriptor);
        }

        Services.AddScoped<IUriProvider, T>();
        return this;
    }

    /// <summary>
    /// Turns on versioning: each upload of a name stores a new version and keeps up to <paramref name="maxVersions"/>
    /// of them, pruning the oldest. Without versioning, a name holds one version, which an upload with
    /// <see cref="ResourceUploadOptions.Overwrite"/> replaces.
    /// </summary>
    /// <param name="maxVersions">How many versions of each name to keep, including the current one; 100 when null.</param>
    /// <exception cref="InvalidOperationException"><paramref name="maxVersions"/> is zero or negative.</exception>
    public ResourcefullOptionsBuilder UseVersioning(int? maxVersions = null)
    {
        if (maxVersions <= 0)
        {
            throw new InvalidOperationException($"Value for {nameof(maxVersions)} must be greater than zero.");
        }

        Services.Configure<ResourcefullOptions>(options =>
        {
            options.IsVersioningEnabled = true;
            options.MaxVersions = maxVersions ?? 100;
        });
        return this;
    }

    /// <summary>
    /// Configures what Resourcefull's spans and metrics record. Telemetry is published whether or not this is called,
    /// under the names in <see cref="ResourcefullTelemetry"/>; this only changes what goes into it.
    /// </summary>
    /// <param name="configure">A callback to set <see cref="ResourcefullTelemetryOptions"/>.</param>
    public ResourcefullOptionsBuilder UseTelemetry(Action<ResourcefullTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure<ResourcefullOptions>(options => configure(options.Telemetry));
        return this;
    }
}