using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Resourcefull;

/// <summary>
/// Adds options for the configuration of Resourcefull service
/// </summary>
public class ResourcefullOptionsBuilder
{
    public bool ProviderConfigured { get; set; }
    public IServiceCollection Services { get; init; }

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
}