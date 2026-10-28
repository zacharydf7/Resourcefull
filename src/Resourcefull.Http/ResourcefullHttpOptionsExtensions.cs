using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Http;

public static class ResourcefullHttpOptionsExtensions
{
    /// <summary>
    /// Configures Resourcefull to use the <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/> service to include HTTP context information when generating URIs for resources.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static ResourcefullOptionsBuilder UseHttpContext(this ResourcefullOptionsBuilder builder)
    {
        ServiceDescriptor? descriptor = builder.Services.SingleOrDefault(
                d => d.ServiceType ==
                    typeof(IUriProvider));

        if (descriptor == null)
        {
            builder.Services.AddScoped<IUriProvider, HttpContextUriProvider>();
        }

        builder.Services.AddHttpContextAccessor();
        return builder;
    }
}