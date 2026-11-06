namespace Resourcefull;

/// <summary>
/// How <see cref="IResourceContainerClient.CopyResourceAsync"/> copies a resource.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceCopyOptions
{
    /// <summary>The version to copy; the current version when <see langword="null"/>.</summary>
    public string? VersionId { get; init; }

    /// <summary>
    /// What the copy does when the destination name already has a current version, as
    /// <see cref="ResourceUploadOptions.Mode"/> does for an upload: <see cref="ResourceWriteMode.Create"/>, the
    /// default, refuses it, and <see cref="ResourceWriteMode.Replace"/> stores the copy as the new current version.
    /// </summary>
    public ResourceWriteMode Mode { get; init; }

    /// <summary>
    /// What the destination name's current version must be like, decided by the copy itself, as
    /// <see cref="ResourceUploadOptions.Conditions"/> are for an upload. They need <see cref="Mode"/> set to
    /// <see cref="ResourceWriteMode.Replace"/>.
    /// </summary>
    public ResourceConditions? DestinationConditions { get; init; }

    /// <summary>
    /// When the copy expires, as <see cref="ResourceUploadOptions.ExpiresAt"/> for an upload; the source's expiry isn't
    /// copied, so <see langword="null"/> never expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Until when the copy can't be deleted, as <see cref="ResourceUploadOptions.RetainUntil"/> for an upload; the
    /// source's isn't copied, so <see langword="null"/> takes the destination's
    /// <see cref="ContainerPolicy.RetainFor"/>, if it has one.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? RetainUntil { get; init; }

    /// <summary>
    /// Whether the copy is stored under a legal hold, as <see cref="ResourceUploadOptions.LegalHold"/> for an upload; the
    /// source's isn't copied.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public bool LegalHold { get; init; }
}

/// <summary>
/// How <see cref="IResourceContainerClient.MoveResourceAsync"/> moves a resource.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceMoveOptions
{
    /// <summary>What the resource's current version must be like, decided by the move itself.</summary>
    public ResourceConditions? SourceConditions { get; init; }
}

/// <summary>
/// The version a copy or restore was made from. It's a record of where the content came from, not a link: it stays
/// as it is when the source is later moved or deleted.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The source's container.</param>
/// <param name="ResourceName">The source's name.</param>
/// <param name="VersionId">The source version's ID.</param>
public sealed record ResourceOrigin(string ContainerName, string ResourceName, string VersionId);
