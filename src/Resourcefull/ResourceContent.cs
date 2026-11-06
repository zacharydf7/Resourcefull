namespace Resourcefull;

/// <summary>
/// The result of downloading a resource's content into memory, pairing the resource's
/// <see cref="ResourceInfo"/> with its bytes. Use this for small resources; for large payloads prefer
/// <see cref="IResourceContainerClient.DownloadStreamingAsync"/> to avoid buffering the whole resource.
/// </summary>
public sealed record ResourceContent
{
    /// <summary>The attributes of the downloaded resource.</summary>
    public required ResourceInfo ResourceInfo { get; init; }

    /// <summary>The resource's content.</summary>
    public required BinaryData Content { get; init; }
}
