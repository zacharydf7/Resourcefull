namespace Resourcefull;

/// <summary>
/// Describes the content of a resource.
/// </summary>
public sealed record ResourceHeaders
{
    /// <summary>The media type of the resource.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The size of the resource in bytes, optional. When given, it must match the bytes uploaded or the upload throws
    /// <see cref="ResourceContentLengthMismatchException"/> and stores nothing.
    /// </summary>
    public long? ContentLength { get; init; }
}