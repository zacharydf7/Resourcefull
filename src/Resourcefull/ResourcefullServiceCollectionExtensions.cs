using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>
/// Registers Resourcefull with an application's services.
/// </summary>
public static class ResourcefullServiceCollectionExtensions
{
    /// <summary>
    /// Adds Resourcefull: <see cref="IResourceServiceClient"/> as a scoped service, configured by
    /// <paramref name="configure"/>, which must choose exactly one storage provider. To add more stores beside it, use
    /// <see cref="AddKeyedResourcefull"/>.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">A callback to configure <see cref="ResourcefullOptionsBuilder"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="configure"/> configures no provider, or more than one, or Resourcefull is already added.
    /// </exception>
    public static IServiceCollection AddResourcefull(this IServiceCollection services, Action<ResourcefullOptionsBuilder> configure)
        => AddStore(services, null, configure);

    /// <summary>
    /// Adds a store named <paramref name="storeName"/>, with its own provider and settings, beside any others: for example,
    /// hot and archive storage, or a database for each tenant. Resolve its <see cref="IResourceServiceClient"/> as a
    /// keyed service, such as with <c>[FromKeyedServices("archive")]</c>.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="storeName">The store's name, its service key.</param>
    /// <param name="configure">A callback to configure <see cref="ResourcefullOptionsBuilder"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="storeName"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="configure"/> configures no provider, or more than one, or a store with this name is already added.
    /// </exception>
    public static IServiceCollection AddKeyedResourcefull(this IServiceCollection services, string storeName,
        Action<ResourcefullOptionsBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        return AddStore(services, storeName, configure);
    }

    private static IServiceCollection AddStore(IServiceCollection services, string? name, Action<ResourcefullOptionsBuilder> configure)
    {
        // A second call would replace some of the first one's settings and keep the rest.
        if (services.Any(d => d.ServiceType == typeof(IResourceServiceClient) && Equals(d.ServiceKey, name)))
        {
            throw new InvalidOperationException(name is null
                ? "Resourcefull is already added. Add other stores with AddKeyedResourcefull."
                : $"A Resourcefull store named '{name}' is already added.");
        }

        var builder = new ResourcefullOptionsBuilder(services, name);
        services.AddOptions();
        if (name is not null) services.Configure<ResourcefullOptions>(name, options => options.StoreName = name);
        services.Add(new ServiceDescriptor(typeof(IResourceServiceClient), name, (provider, _) =>
        {
            ResourcefullOptions options = provider.GetRequiredService<IOptionsMonitor<ResourcefullOptions>>().Get(builder.OptionsName);
            ResourceRepository repository = provider.GetRequiredStoreService<ResourceRepository>(name);
            return new ResourceServiceClient(repository, Options.Create(options), provider.GetStoreService<IResourceUriProvider>(name),
                provider.GetService<ILogger<ResourceContainerClient>>(), provider.GetStoreService<ContainerPolicies>(name),
                provider.GetStoreService<IResourceCleanupHandler>(name), provider.GetService<TimeProvider>(),
                provider.GetStoreService<ExpirySettings>(name), provider.GetStoreService<IResourceUploadInspector>(name));
        }, ServiceLifetime.Scoped));
        configure.Invoke(builder);

        if (!builder.ProviderConfigured)
        {
            throw new InvalidOperationException("No provider configured.");
        }
        builder.ThrowIfInconsistent();

        return services;
    }
}