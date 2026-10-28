namespace Resourcefull;

/// <summary>
/// The length and hash of the bytes a repository wrote.
/// </summary>
internal record WrittenContent(long ContentLength, string ContentHash);
