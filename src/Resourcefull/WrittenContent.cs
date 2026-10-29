namespace Resourcefull;

/// <summary>
/// What a repository wrote, as returned by <see cref="IResourceRepository.AddResourceAsync(string, ResourceInfo, Stream, bool, CancellationToken)"/>: the length and hash of
/// the bytes stored, and when the version became current.
/// </summary>
/// <param name="ContentLength">The number of bytes stored.</param>
/// <param name="ContentHash">The SHA-256 of the bytes stored, as <c>sha256:</c> followed by lowercase hex.</param>
/// <param name="LastModified">
/// When the version became current, by the repository's clock; stored as the version's
/// <see cref="ResourceInfo.LastModified"/>.
/// </param>
public record WrittenContent(long ContentLength, string ContentHash, DateTimeOffset LastModified);
