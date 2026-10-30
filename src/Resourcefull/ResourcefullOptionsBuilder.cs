using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Resourcefull;

/// <summary>
/// Configures Resourcefull inside <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/>: its storage
/// provider, versioning, URI provider, telemetry, container policies and expiry.
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
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="configure">A callback to set <see cref="ResourcefullTelemetryOptions"/>.</param>
    public ResourcefullOptionsBuilder UseTelemetry(Action<ResourcefullTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure<ResourcefullOptions>(options => configure(options.Telemetry));
        return this;
    }

    /// <summary>
    /// Turns on container policies, with <paramref name="defaults"/> for every container, replacing any set before.
    /// Add <see cref="UsePolicyProvider{TProvider}"/> to give containers their own. Without either, no policy is checked.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="defaults">The policy every container has, unless its own says otherwise; none when null.</param>
    /// <param name="cacheFor">
    /// How long a container's policy is kept before the policy provider is asked again, so how long a change can take
    /// to apply: 5 seconds when null, and zero to ask on every write.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cacheFor"/> is negative.</exception>
    public ResourcefullOptionsBuilder UseContainerPolicies(ResourceContainerPolicy? defaults = null, TimeSpan? cacheFor = null)
    {
        if (cacheFor < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cacheFor), cacheFor, "The cache time can't be negative.");

        Services.RemoveAll<ContainerPolicySettings>();
        Services.AddSingleton(_ => new ContainerPolicySettings(defaults ?? new ResourceContainerPolicy(), cacheFor ?? TimeSpan.FromSeconds(5)));
        Services.TryAddScoped<ContainerPolicies>();
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="TProvider"/> to give containers their own policies, replacing any policy provider set
    /// before, and turns on container policies with no defaults if <see cref="UseContainerPolicies"/> hasn't. It's
    /// registered as a scoped service, so it can depend on other services, such as the application's database context.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <typeparam name="TProvider">The <see cref="IResourcePolicyProvider"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UsePolicyProvider<TProvider>() where TProvider : class, IResourcePolicyProvider
    {
        Services.RemoveAll<IResourcePolicyProvider>();
        Services.AddScoped<IResourcePolicyProvider, TProvider>();
        if (!Services.Any(d => d.ServiceType == typeof(ContainerPolicySettings))) UseContainerPolicies();
        return this;
    }

    /// <summary>
    /// Deletes expired versions in the background: a hosted service calls
    /// <see cref="IResourceServiceClient.DeleteExpiredAsync"/> when the application starts and every
    /// <paramref name="interval"/> after, so a version is gone within one interval of its
    /// <see cref="ResourceInfo.ExpiresAt"/>. Every instance of the application can run it. With a provider that doesn't
    /// support expiry, it logs a warning and stops. It also changes pruning: a version past
    /// <see cref="ResourcefullOptions.MaxVersions"/>, or replaced by an overwrite, is set to expire five minutes later
    /// instead of being deleted at once, so a download already reading it can finish.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <param name="interval">How often to look for expired versions; one minute when null.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> isn't positive.</exception>
    public ResourcefullOptionsBuilder UseExpiry(TimeSpan? interval = null)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The interval must be positive.");

        Services.RemoveAll<ExpirySettings>();
        Services.AddSingleton(new ExpirySettings(interval ?? TimeSpan.FromMinutes(1)));
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ExpiryService>());
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="THandler"/> to hear about every version Resourcefull deletes by itself, before it's
    /// deleted, replacing any cleanup handler set before. It's registered as a scoped service, so it can depend on other
    /// services, such as the application's audit client.
    /// <para>Coming in 3.2.</para>
    /// </summary>
    /// <typeparam name="THandler">The <see cref="IResourceCleanupHandler"/> implementation.</typeparam>
    public ResourcefullOptionsBuilder UseCleanupHandler<THandler>() where THandler : class, IResourceCleanupHandler
    {
        Services.RemoveAll<IResourceCleanupHandler>();
        Services.AddScoped<IResourceCleanupHandler, THandler>();
        return this;
    }
}