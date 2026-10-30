namespace Resourcefull;

/// <summary>How <see cref="IResourceContainerClient.CopyResourceAsync"/> copies a resource.</summary>
public sealed class ResourceCopyOptions
{
    /// <summary>The version to copy; the current version when <see langword="null"/>.</summary>
    public string? VersionId { get; init; }

    /// <summary>
    /// Whether the copy may replace an existing resource at the destination when versioning is off. With versioning
    /// on, a copy to an existing name is always stored as its new current version, as an upload would be.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// What the destination name's current version must be like, decided by the copy itself, as
    /// <see cref="ResourceUploadOptions.Conditions"/> are for an upload.
    /// </summary>
    public ResourceConditions? DestinationConditions { get; init; }
}

/// <summary>How <see cref="IResourceContainerClient.MoveResourceAsync"/> moves a resource.</summary>
public sealed class ResourceMoveOptions
{
    /// <summary>What the resource's current version must be like, decided by the move itself.</summary>
    public ResourceConditions? SourceConditions { get; init; }
}

/// <summary>A resource name in a container.</summary>
/// <param name="ContainerName">The container's name.</param>
/// <param name="ResourceName">The resource's name.</param>
public readonly record struct ResourceLocation(string ContainerName, string ResourceName);

/// <summary>
/// The version a copy or restore was made from. It's a record of where the content came from, not a link: it stays
/// as it is when the source is later moved or deleted.
/// </summary>
/// <param name="ContainerName">The source's container.</param>
/// <param name="ResourceName">The source's name.</param>
/// <param name="VersionId">The source version's ID.</param>
public sealed record ResourceOrigin(string ContainerName, string ResourceName, string VersionId);
