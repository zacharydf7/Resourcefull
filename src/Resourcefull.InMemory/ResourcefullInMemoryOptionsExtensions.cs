using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.InMemory;

public static class ResourcefullInMemoryOptionsExtensions
{
    /// <summary>
    /// Configures Resourcefull to use an in-memory data provider.
    /// The in-memory provider is intended for testing use cases and should not be used in production environments.
    /// It is not yet safe for concurrent use, so call it from one caller at a time.
    /// </summary>
    /// <param name="builder"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public static ResourcefullOptionsBuilder UseInMemory(this ResourcefullOptionsBuilder builder)
        => builder.UseProvider<ResourceInMemoryRepository>(ServiceLifetime.Singleton);
}
