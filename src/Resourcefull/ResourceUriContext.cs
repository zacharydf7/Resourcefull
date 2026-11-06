namespace Resourcefull;

/// <summary>
/// What an <see cref="IResourceUriProvider"/> gives a URI for: a container, a resource in it, or a version of that
/// resource.
/// </summary>
/// <param name="ContainerName">The container's name.</param>
/// <param name="ResourceName">The resource's name; <see langword="null"/> for the container itself.</param>
/// <param name="VersionId">The version's ID; <see langword="null"/> when no version is meant.</param>
public sealed record ResourceUriContext(string ContainerName, string? ResourceName = null, string? VersionId = null);