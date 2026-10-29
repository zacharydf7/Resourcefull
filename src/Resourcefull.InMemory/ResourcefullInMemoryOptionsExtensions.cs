using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.InMemory;

/// <summary>
/// Configures Resourcefull to keep resources in process memory.
/// </summary>
public static class ResourcefullInMemoryOptionsExtensions
{
    /// <summary>
    /// Configures Resourcefull to use an in-memory data provider.
    /// The in-memory provider is intended for testing use cases and should not be used in production environments.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public static ResourcefullOptionsBuilder UseInMemory(this ResourcefullOptionsBuilder builder)
        => builder.UseProvider<ResourceInMemoryRepository>(ServiceLifetime.Singleton);
}
