using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>
/// Conditions on a name's current version that an upload or delete must meet to go ahead. They're checked by the write
/// itself, as it would make its change, so a concurrent change to the name can't slip in between the check and the
/// write; when they aren't met, nothing changes and <see cref="ResourcePreconditionFailedException"/> is thrown.
/// A name with no current version meets no condition. When both are set, both must hold.
/// </summary>
public sealed class ResourceConditions
{
    /// <summary>Go ahead only if the name's current version has this <see cref="ResourceInfo.VersionId"/>.</summary>
    public string? IfMatchVersionId { get; init; }

    /// <summary>
    /// Go ahead only if the name's current version has this <see cref="ResourceInfo.ContentHash"/>
    /// (<c>sha256:…</c>). When it comes from an HTTP <c>If-Match</c> header whose entity tag is the content hash, pass
    /// the tag without its quotes. A version stored before 3.0 has no hash until <c>ContentHashBackfill</c> records
    /// one, so it meets no hash condition.
    /// </summary>
    public string? IfMatchContentHash { get; init; }

    /// <summary>
    /// Whether neither condition is set. Empty conditions are no conditions: every write meets them, so a provider can
    /// skip reading the current version to check them.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public bool IsEmpty => IfMatchVersionId is null && IfMatchContentHash is null;

    /// <summary>
    /// Throws unless the name's current version meets these conditions. A provider calls it in the write itself, with
    /// the current version the write read. A name with no current version, given as a <see langword="null"/>
    /// <paramref name="currentVersionId"/>, meets no condition; empty conditions are always met.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="resource">The name the conditions are on.</param>
    /// <param name="currentVersionId">The ID of the name's current version, or <see langword="null"/> if it has none.</param>
    /// <param name="currentContentHash">The content hash of the name's current version, if it has one.</param>
    /// <exception cref="ResourcePreconditionFailedException">
    /// The conditions aren't met. It carries <paramref name="currentVersionId"/> and <paramref name="currentContentHash"/>,
    /// and names <paramref name="resource"/>.
    /// </exception>
    public void ThrowIfNotMet(ResourceLocation resource, string? currentVersionId, string? currentContentHash)
    {
        if (IsEmpty) return;

        (string resourceContainerName, string resourceName) = resource;
        if (currentVersionId is null
            || (IfMatchVersionId is { } id && id != currentVersionId)
            || (IfMatchContentHash is { } hash && hash != currentContentHash))
        {
            throw new ResourcePreconditionFailedException(currentVersionId is null
                ? $"The resource {resourceName} in the container {resourceContainerName} has no current version to " +
                    "meet the conditions."
                : $"The current version {currentVersionId} of the resource {resourceName} in the container " +
                    $"{resourceContainerName} doesn't meet the conditions.",
                currentVersionId, currentContentHash)
            {
                ContainerName = resourceContainerName, ResourceName = resourceName
            };
        }
    }
}

