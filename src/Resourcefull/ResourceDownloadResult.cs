namespace Resourcefull;

/// <summary>
/// The result of a streaming download: the version's attributes and a stream of its content. The result owns the stream,
/// and disposing the result disposes the stream.
/// </summary>
/// <param name="resourceInfo">The attributes of the downloaded version.</param>
/// <param name="stream">
/// The content, or the range of it that was asked for, read-only and forward-only; it's read from the store as you read
/// it. The result takes ownership of it.
/// </param>
/// <param name="totalLength">
/// The length of the version's whole content, measured from what's stored, even when <paramref name="stream"/> holds a
/// range of it.
/// </param>
public sealed class ResourceDownloadResult(ResourceInfo resourceInfo, Stream stream, long totalLength) : IDisposable, IAsyncDisposable
{
    /// <summary>The attributes of the downloaded version.</summary>
    public ResourceInfo ResourceInfo { get; } = resourceInfo;

    /// <summary>
    /// The content, or the range of it that was asked for, read-only and forward-only; it's read from the store as you
    /// read it. The caller disposes it, directly or by disposing this result.
    /// </summary>
    public Stream Stream { get; } = stream;

    /// <summary>
    /// The length of the version's whole content, measured from what's stored, even when <see cref="Stream"/> holds a
    /// range of it.
    /// </summary>
    public long TotalLength { get; } = totalLength;

    /// <summary>
    /// The bytes <see cref="Stream"/> holds, cut at the end of the content, so its <see cref="ResourceRange.Length"/> is
    /// set; <see langword="null"/> when it holds the whole content.
    /// </summary>
    public ResourceRange? Range { get; init; }

    /// <summary>
    /// Disposes <see cref="Stream"/>.
    /// </summary>
    public void Dispose() => Stream.Dispose();

    /// <summary>
    /// Disposes <see cref="Stream"/>.
    /// </summary>
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}