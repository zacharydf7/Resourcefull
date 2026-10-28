namespace Resourcefull;

/// <summary>
/// Describes the content of a resource.
/// </summary>
/// <param name="ContentType">The media type of the resource.</param>
/// <param name="ContentLength">The size of the resource in bytes.</param>
public record ResourceHeaders(string? ContentType, long? ContentLength);