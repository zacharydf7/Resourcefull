namespace Resourcefull;

/// <summary>
/// Represents a container for the results of a download request.
/// </summary>
/// <param name="ResourceInfo"></param>
/// <param name="Stream">A <see cref="System.IO.Stream"/> containing the retrieved resource.</param>
public record ResourceDownloadResult(ResourceInfo ResourceInfo, Stream Stream);