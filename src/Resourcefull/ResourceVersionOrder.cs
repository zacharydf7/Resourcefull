namespace Resourcefull;

/// <summary>
/// Which versions <see cref="IResourceContainerClient.GetTopVersionsAsync"/> lists first.
/// </summary>
public enum ResourceVersionOrder
{
    /// <summary>
    /// The largest first, by <see cref="ResourceInfo.ContentLength"/>, current and older versions alike: what's taking
    /// up room in a container. Versions of the same size are in version ID order, and a version without a recorded
    /// length comes after every version with one.
    /// </summary>
    LargestFirst,

    /// <summary>
    /// The ones that expire soonest first, by <see cref="ResourceInfo.ExpiresAt"/>, starting with any already expired that
    /// the sweep hasn't deleted yet. Versions that expire at the same time are in version ID order, and versions without
    /// an expiry aren't listed.
    /// </summary>
    ExpiringFirst
}
