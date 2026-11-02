namespace Resourcefull;

/// <summary>
/// The result of downloading a resource's content into memory, pairing the resource's
/// <see cref="ResourceInfo"/> with its bytes. Use this for small resources; for large payloads prefer
/// <see cref="IResourceContainerClient.DownloadStreamingAsync"/> to avoid buffering the whole resource.
/// </summary>
/// <param name="ResourceInfo">The attributes of the downloaded resource.</param>
/// <param name="Content">The resource's content.</param>
public record ResourceContent(ResourceInfo ResourceInfo, BinaryData Content);
