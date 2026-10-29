namespace Resourcefull;

/// <summary>
/// The result of a streaming download: the version's attributes and a stream of its content.
/// </summary>
/// <param name="ResourceInfo">The attributes of the downloaded version.</param>
/// <param name="Stream">
/// The content, or the range of it that was asked for, read-only and forward-only; it is read from the store as it is
/// read. The caller disposes it.
/// </param>
public record ResourceDownloadResult(ResourceInfo ResourceInfo, Stream Stream)
{
    /// <summary>
    /// The length of the version's whole content, measured from what's stored, even when <see cref="Stream"/> holds a
    /// range of it.
    /// </summary>
    public long TotalLength { get; init; }

    /// <summary>
    /// The bytes <see cref="Stream"/> holds, cut at the end of the content, so its <see cref="ResourceRange.Length"/> is
    /// set; <see langword="null"/> when it holds the whole content.
    /// </summary>
    public ResourceRange? Range { get; init; }
}