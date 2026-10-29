namespace Resourcefull;

/// <summary>
/// The result of a streaming download: the version's attributes and a stream of its content.
/// </summary>
/// <param name="ResourceInfo">The attributes of the downloaded version.</param>
/// <param name="Stream">
/// The content, read-only and forward-only; it is read from the store as it is read. The caller disposes it.
/// </param>
public record ResourceDownloadResult(ResourceInfo ResourceInfo, Stream Stream);