namespace Resourcefull.Providers;

/// <summary>
/// What a repository wrote, as returned by <see cref="ResourceRepository.AddResourceAsync"/>: the length and hash of
/// the bytes stored, and when the version became current.
/// </summary>
public sealed record WrittenContent
{
    /// <summary>The number of bytes stored.</summary>
    public required long ContentLength { get; init; }

    /// <summary>The SHA-256 of the bytes stored, as <c>sha256:</c> followed by lowercase hex.</summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// When the version became current, by the repository's clock; stored as the version's
    /// <see cref="ResourceInfo.LastModified"/>.
    /// </summary>
    public required DateTimeOffset LastModified { get; init; }
}
