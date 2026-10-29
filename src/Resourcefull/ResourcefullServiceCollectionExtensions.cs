using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull;

/// <summary>
/// Registers Resourcefull with an application's services.
/// </summary>
public static class ResourcefullServiceCollectionExtensions
{
    /// <summary>
    /// Adds Resourcefull: <see cref="IResourceServiceClient"/> as a scoped service, configured by
    /// <paramref name="configure"/>, which must choose exactly one storage provider.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="configure">A callback to configure <see cref="ResourcefullOptionsBuilder"/>.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="configure"/> configures no provider, or more than one.</exception>
    public static IServiceCollection AddResourcefull(this IServiceCollection services, Action<ResourcefullOptionsBuilder> configure)
    {
        var builder = new ResourcefullOptionsBuilder(services);

        ServiceDescriptor? descriptor = builder.Services.SingleOrDefault(
                d => d.ServiceType ==
                    typeof(IResourceServiceClient));

        if (descriptor != null)
        {
            builder.Services.Remove(descriptor);
        }

        builder.Services.AddOptions();
        builder.Services.AddScoped<IResourceServiceClient, ResourceServiceClient>();
        configure.Invoke(builder);

        if (!builder.ProviderConfigured)
        {
            throw new InvalidOperationException("No provider configured.");
        }

        return services;
    }
}