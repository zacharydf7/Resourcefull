namespace Resourcefull.Providers;

/// <summary>
/// One page of a container's bin, as <see cref="ResourceRepository.GetDeletedPageAsync"/> returns it: the versions
/// found, newest deletion first, and where the next page starts.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Versions">
/// The deleted versions found, each with <see cref="ResourceInfo.DeletedAt"/> and <see cref="ResourceInfo.PurgeAt"/>
/// set. With a prefix, there may be fewer than were asked for even when more follow.
/// </param>
/// <param name="Next">
/// The deletion time and version ID to continue after, past every version this page looked at, including those the
/// prefix left out; <see langword="null"/> when the bin has nothing more.
/// </param>
public sealed record DeletedVersionPage(IReadOnlyList<ResourceInfo> Versions, (DateTimeOffset DeletedAt, string VersionId)? Next);
