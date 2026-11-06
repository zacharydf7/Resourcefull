namespace Resourcefull.Providers;

/// <summary>
/// One page of a container's bin, as <see cref="ResourceRepository.GetDeletedPageAsync"/> returns it: the versions
/// found, newest deletion first, and where the next page starts.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed record DeletedVersionPage
{
    /// <summary>
    /// The deleted versions found, each with <see cref="ResourceInfo.DeletedAt"/> and <see cref="ResourceInfo.PurgeAt"/>
    /// set. With a prefix, there may be fewer than were asked for even when more follow.
    /// </summary>
    public required IReadOnlyList<ResourceInfo> Versions { get; init; }

    /// <summary>
    /// Where the next page continues, past every version this page looked at, including those the prefix left out;
    /// <see langword="null"/> when the bin has nothing more. Its <see cref="ResourceVersionKey.At"/> is the deletion time.
    /// </summary>
    public required ResourceVersionKey? Next { get; init; }
}
