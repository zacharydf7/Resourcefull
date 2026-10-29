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

    internal bool IsEmpty => IfMatchVersionId is null && IfMatchContentHash is null;
}

/// <summary>
/// What an <see cref="IResourceRepository.AddResourceAsync(string, ResourceInfo, Stream, ResourceWriteCondition, CancellationToken)"/>
/// write requires of the name before it stores a version.
/// </summary>
public sealed class ResourceWriteCondition
{
    /// <summary>Store only if no version of the name exists.</summary>
    public bool CreateOnly { get; init; }

    /// <summary>Store only if the name's current version meets these.</summary>
    public ResourceConditions? Conditions { get; init; }
}
