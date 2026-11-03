namespace Resourcefull.Providers;

/// <summary>
/// A resource name in a container, as a provider's members take it.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The container's name.</param>
/// <param name="ResourceName">The resource's name.</param>
public readonly record struct ResourceLocation(string ContainerName, string ResourceName);
