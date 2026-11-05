namespace Resourcefull.Providers;

/// <summary>
/// Checks and exceptions a provider uses in its writes and deletes, so that it refuses what the built-in providers
/// refuse, with the same messages.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullProviderExtensions
{
    extension(ResourceConditions conditions)
    {
        /// <summary>
        /// Throws unless the name's current version meets these conditions. A provider calls it in the write itself, with
        /// the current version the write read. A name with no current version, given as a <see langword="null"/>
        /// <paramref name="currentVersionId"/>, meets no condition; empty conditions are always met.
        /// </summary>
        /// <param name="resource">The name the conditions are on.</param>
        /// <param name="currentVersionId">The ID of the name's current version, or <see langword="null"/> if it has none.</param>
        /// <param name="currentContentHash">The content hash of the name's current version, if it has one.</param>
        /// <exception cref="ResourcePreconditionFailedException">
        /// The conditions aren't met. It carries <paramref name="currentVersionId"/> and <paramref name="currentContentHash"/>,
        /// and names <paramref name="resource"/>.
        /// </exception>
        public void ThrowIfNotMet(ResourceAddress resource, string? currentVersionId, string? currentContentHash)
        {
            if (conditions.IsEmpty) return;

            (string resourceContainerName, string resourceName) = resource;
            if (currentVersionId is null
                || (conditions.IfMatchVersionId is { } id && id != currentVersionId)
                || (conditions.IfMatchContentHash is { } hash && hash != currentContentHash))
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

    extension(ResourceRetainedException)
    {
        /// <summary>
        /// The exception for a version that can't be deleted at <paramref name="now"/>, with the message the clients and
        /// the built-in providers use, for a provider to throw when its delete finds one.
        /// </summary>
        /// <param name="resource">The version's container and name.</param>
        /// <param name="versionId">The version.</param>
        /// <param name="retainUntil">The version's <see cref="ResourceInfo.RetainUntil"/>.</param>
        /// <param name="legalHold">The version's <see cref="ResourceInfo.LegalHold"/>.</param>
        /// <param name="now">The time the delete judged it by.</param>
        public static ResourceRetainedException Create(ResourceAddress resource, string versionId, DateTimeOffset? retainUntil,
            bool legalHold, DateTimeOffset now)
        {
            DateTimeOffset? until = retainUntil > now ? retainUntil : null;
            string why = (until, legalHold) switch
            {
                ({ } date, true) => $"it's retained until {date:O} and under a legal hold",
                ({ } date, false) => $"it's retained until {date:O}",
                _ => "it's under a legal hold"
            };
            return new ResourceRetainedException($"The version {versionId} of the resource {resource.ResourceName} in the " +
                $"container {resource.ContainerName} can't be deleted: {why}.", versionId, until, legalHold)
            {
                ContainerName = resource.ContainerName, ResourceName = resource.ResourceName
            };
        }
    }
}
