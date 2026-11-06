namespace Resourcefull.Providers;

/// <summary>
/// A version's position in a listing sorted by time, such as the list of expired versions. The listing sorts by
/// <see cref="At"/>, then ordinally by version ID, then by container and name in the store's name order. It takes all four
/// to identify a position, because version IDs are unique only within a name, and copying a container keeps them.
/// </summary>
/// <param name="At">The time the listing sorts by, such as the version's expiry.</param>
/// <param name="Resource">The version's container and name.</param>
/// <param name="VersionId">The version's ID.</param>
public readonly record struct ResourceVersionKey(DateTimeOffset At, ResourceAddress Resource, string VersionId);
