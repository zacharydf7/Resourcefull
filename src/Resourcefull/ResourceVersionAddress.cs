namespace Resourcefull.Providers;

/// <summary>
/// One version of a resource, as <see cref="ResourceRepository.DeleteExpiredVersionsAsync"/> takes and returns them.
/// </summary>
/// <param name="Resource">The version's container and name.</param>
/// <param name="VersionId">The version's ID.</param>
public readonly record struct ResourceVersionAddress(ResourceAddress Resource, string VersionId);
