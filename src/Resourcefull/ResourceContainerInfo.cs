namespace Resourcefull;

/// <summary>
/// Represents the information about a resource container.
/// </summary>
public class ResourceContainerInfo
{
    /// <summary>
    /// The name of the resource container.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The URI of the resource container, from the URI provider each time the container is returned;
    /// <see langword="null"/> without one.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// The metadata associated with the resource container.
    /// </summary>
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
}
