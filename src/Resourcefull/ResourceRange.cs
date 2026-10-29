namespace Resourcefull;

/// <summary>
/// A byte range of a resource's content: <see cref="Length"/> bytes from <see cref="Offset"/>, or everything from
/// <see cref="Offset"/> to the end when <see cref="Length"/> is <see langword="null"/>.
/// </summary>
/// <param name="Offset">Where the range starts, in bytes from the start of the content.</param>
/// <param name="Length">How many bytes the range holds; to the end of the content when <see langword="null"/>.</param>
public readonly record struct ResourceRange(long Offset, long? Length = null);
