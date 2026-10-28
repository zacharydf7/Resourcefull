namespace Resourcefull;

/// <summary>
/// Describes the content of a resource.
/// </summary>
/// <param name="ContentType">The media type of the resource.</param>
/// <param name="ContentLength">
/// The size of the resource in bytes, optional. When given, it must match the bytes uploaded or the upload throws
/// <see cref="ResourceContentLengthMismatchException"/> and stores nothing.
/// </param>
public record ResourceHeaders(string? ContentType, long? ContentLength);