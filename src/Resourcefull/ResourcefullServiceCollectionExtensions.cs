using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull;

public static class ResourcefullServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Resourcefull service to the service collection.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure">A callback to configure <see cref="ResourcefullOptionsBuilder"/>.</param>
    /// <exception cref="InvalidOperationException"/>
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