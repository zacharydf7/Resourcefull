namespace Resourcefull;

/// <summary>
/// Represents the context of a resourceful operation, including the container name and optional resource name.
/// </summary>
public record ResourcefullContext(string ContainerName, string? ResourceName = null, string? ResourceVersionId = null);