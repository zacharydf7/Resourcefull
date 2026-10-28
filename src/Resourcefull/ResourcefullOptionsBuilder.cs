using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull;

/// <summary>
/// Adds options for the configuration of Resourcefull service
/// </summary>
public class ResourcefullOptionsBuilder
{
    public bool ProviderConfigured { get; set; }
    public IServiceCollection Services { get; init; }

    public ResourcefullOptionsBuilder(IServiceCollection services) => Services = services;

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