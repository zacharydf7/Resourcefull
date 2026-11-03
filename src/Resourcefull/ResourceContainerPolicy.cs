namespace Resourcefull;

/// <summary>
/// Rules for what may be written to a container. Store-wide defaults come from
/// <see cref="ResourcefullOptionsBuilder.UseContainerPolicies"/>, and an <see cref="IResourcePolicyProvider"/> can give
/// any container its own; a property left <see langword="null"/> there falls back to the default. A write that breaks
/// a rule throws <see cref="ResourcePolicyViolationException"/> and stores nothing. Pruning older versions is never
/// refused, and lowering a limit below what a container holds only refuses new writes.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceContainerPolicy
{
    /// <summary>
    /// The largest version an upload or restore may store. A declared length over it is refused before any content
    /// is read; otherwise the write stops as soon as the content passes it.
    /// </summary>
    public ResourceSize? MaxResourceSize { get; init; }

    /// <summary>
    /// The most the container may hold, counting every version of every resource until it's deleted. An upload or
    /// restore is refused before any content is read if its declared length wouldn't fit, and stops as soon as its
    /// content passes what's left. Overwriting a resource needs room for the new content while the old version still
    /// counts, even without versioning. What the container holds is read once per write, so writes running at the same time
    /// can together go slightly over it.
    /// </summary>
    public ResourceSize? MaxContainerSize { get; init; }

    /// <summary>
    /// The most resources (names, whatever their number of versions) the container may hold. An upload of a new name
    /// is refused when the container has this many; a new version of an existing name never is. Like
    /// <see cref="MaxContainerSize"/>, uploads running at the same time can together go slightly over it.
    /// </summary>
    public long? MaxResourceCount { get; init; }

    /// <summary>
    /// The content types an upload may declare, such as <c>application/pdf</c>, or <c>image/*</c> for every image type.
    /// Types are compared without their parameters, ignoring case, and an upload that declares no type is refused.
    /// <see langword="null"/> allows any type, or none.
    /// </summary>
    public IReadOnlyCollection<string>? AllowedContentTypes { get; init; }

    /// <summary>
    /// How many versions of each name to keep, including the current one. An upload with
    /// <see cref="ResourceWriteMode.Replace"/> keeps the version it replaces as an older one until the name has more
    /// than this, and then the oldest are pruned; with 1, the replaced version is deleted. When neither the
    /// container's policy nor the defaults set it, <see cref="ResourcefullOptionsBuilder.UseVersioning"/> decides, and
    /// without that, 1. <see cref="IResourceContainerClient.GetPolicyAsync"/> reports the limit in force.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">It's set to less than 1.</exception>
    public int? MaxVersions
    {
        get => _maxVersions;
        init => _maxVersions = value < 1
            ? throw new ArgumentOutOfRangeException(nameof(MaxVersions), value, "Keep at least one version.")
            : value;
    }

    private readonly int? _maxVersions;

    /// <summary>
    /// Whether the container refuses every change: uploads, restores, deletes, and deleting the container itself.
    /// Reads still work.
    /// </summary>
    public bool? ReadOnly { get; init; }

    /// <summary>
    /// Whether <see cref="AllowedContentTypes"/> lets an upload declare <paramref name="contentType"/>, matched the same
    /// way the policy checks an upload's type. It's <see langword="true"/> for any type, or none, when <see cref="AllowedContentTypes"/> is
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="contentType">The type an upload declares, parameters included, or <see langword="null"/> for none.</param>
    public bool AllowsContentType(string? contentType) =>
        AllowedContentTypes is null || ContainerPolicies.Allows(AllowedContentTypes, contentType);
}

/// <summary>
/// Gives containers their own <see cref="ResourceContainerPolicy"/>, such as from each user's plan in the application's
/// database. Register one with <see cref="ResourcefullOptionsBuilder.UsePolicyProvider{TProvider}"/>; it's a scoped
/// service, so it can depend on the application's own. Its answer for a container is cached for the time
/// <see cref="ResourcefullOptionsBuilder.UseContainerPolicies"/> sets, so a change applies within that time on every
/// instance.
/// <para>Coming in 4.0.</para>
/// </summary>
public interface IResourcePolicyProvider
{
    /// <summary>Gets a container's own policy.</summary>
    /// <param name="containerName">The container's name; the container may not exist.</param>
    /// <param name="cancellationToken">Cancels the lookup, and the operation that needs it.</param>
    /// <returns>
    /// The container's policy, whose <see langword="null"/> properties fall back to the defaults; or
    /// <see langword="null"/> for the defaults alone.
    /// </returns>
    ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken);
}

/// <summary>
/// The rule of a <see cref="ResourceContainerPolicy"/> that a write broke.
/// <para>Coming in 4.0.</para>
/// </summary>
public enum ResourcePolicyRule
{
    /// <summary><see cref="ResourceContainerPolicy.MaxResourceSize"/>: the content is too large.</summary>
    MaxResourceSize,

    /// <summary><see cref="ResourceContainerPolicy.MaxContainerSize"/>: the container has no room for the content.</summary>
    MaxContainerSize,

    /// <summary><see cref="ResourceContainerPolicy.MaxResourceCount"/>: the container has no room for another name.</summary>
    MaxResourceCount,

    /// <summary><see cref="ResourceContainerPolicy.AllowedContentTypes"/>: the upload's content type isn't allowed.</summary>
    AllowedContentTypes,

    /// <summary><see cref="ResourceContainerPolicy.ReadOnly"/>: the container is read-only.</summary>
    ReadOnly
}

/// <summary>
/// What a container holds.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="StoredSize">
/// The size of every version of every resource in it. A version stored by a release before 3.0 counts its recorded
/// length, which an application may have declared wrongly.
/// </param>
/// <param name="ResourceCount">How many resources (names) it holds.</param>
/// <param name="VersionCount">How many versions it holds, current and older.</param>
public sealed record ResourceContainerUsage(ResourceSize StoredSize, long ResourceCount, long VersionCount);
