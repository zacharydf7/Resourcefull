namespace Resourcefull;

/// <summary>
/// The length and hash of the bytes a repository wrote, as returned by <see cref="IResourceRepository.AddResourceAsync"/>.
/// </summary>
/// <param name="ContentLength">The number of bytes stored.</param>
/// <param name="ContentHash">The SHA-256 of the bytes stored, as <c>sha256:</c> followed by lowercase hex.</param>
public record WrittenContent(long ContentLength, string ContentHash);
