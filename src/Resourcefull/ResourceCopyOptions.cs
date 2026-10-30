namespace Resourcefull;

/// <summary>How <see cref="IResourceContainerClient.CopyResourceAsync"/> copies a resource. <para>Coming in 3.2.</para></summary>
public sealed class ResourceCopyOptions
{
    /// <summary>The version to copy; the current version when <see langword="null"/>.</summary>
    public string? VersionId { get; init; }

    /// <summary>
    /// Whether the copy may replace an existing resource at the destination when versioning is off. With versioning
    /// on, a copy to an existing name is stored as its new current version, as an upload would be, unless
    /// <see cref="CreateOnly"/> is set.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Copy only if no resource with the destination name exists yet, whether or not versioning is enabled, as
    /// <see cref="ResourceUploadOptions.CreateOnly"/> for an upload; otherwise <see cref="ResourceConflictException"/>
    /// is thrown and nothing is stored. Takes precedence over <see cref="Overwrite"/>, and can't be combined with
    /// <see cref="DestinationConditions"/>.
    /// </summary>
    public bool CreateOnly { get; init; }

    /// <summary>
    /// What the destination name's current version must be like, decided by the copy itself, as
    /// <see cref="ResourceUploadOptions.Conditions"/> are for an upload.
    /// </summary>
    public ResourceConditions? DestinationConditions { get; init; }

    /// <summary>
    /// When the copy expires, as <see cref="ResourceUploadOptions.ExpiresAt"/> for an upload; the source's expiry isn't
    /// copied, so <see langword="null"/> never expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>How <see cref="IResourceContainerClient.MoveResourceAsync"/> moves a resource. <para>Coming in 3.2.</para></summary>
public sealed class ResourceMoveOptions
{
    /// <summary>What the resource's current version must be like, decided by the move itself.</summary>
    public ResourceConditions? SourceConditions { get; init; }
}

/// <summary>A resource name in a container. <para>Coming in 3.2.</para></summary>
/// <param name="ContainerName">The container's name.</param>
/// <param name="ResourceName">The resource's name.</param>
public readonly record struct ResourceLocation(string ContainerName, string ResourceName);

/// <summary>
/// The version a copy or restore was made from. It's a record of where the content came from, not a link: it stays
/// as it is when the source is later moved or deleted.
/// <para>Coming in 3.2.</para>
/// </summary>
/// <param name="ContainerName">The source's container.</param>
/// <param name="ResourceName">The source's name.</param>
/// <param name="VersionId">The source version's ID.</param>
public sealed record ResourceOrigin(string ContainerName, string ResourceName, string VersionId);
