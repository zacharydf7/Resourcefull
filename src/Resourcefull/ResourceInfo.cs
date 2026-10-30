namespace Resourcefull;

/// <summary>
/// Describes the attributes of a stored resource.
/// </summary>
public class ResourceInfo
{
    /// <summary>
    /// The container name of a stored resource.
    /// </summary>
    public required string ContainerName { get; set; }

    /// <summary>
    /// The unique name of a stored resource.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The version ID of a stored resource: an opaque, unique string. Compare it for equality only; its format is not
    /// part of the contract.
    /// </summary>
    public string? VersionId { get; set; }

    /// <summary>
    /// When the version became current, by the clock of the application server that stored it. Informational only:
    /// which version is current, and the order versions are listed in, are decided by the store.
    /// </summary>
    public DateTimeOffset LastModified { get; set; }

    /// <summary>
    /// Indicates whether this is the current (most recently stored) version of the resource. When listing
    /// versions, exactly one entry has this set to <see langword="true"/>.
    /// </summary>
    public bool IsLatest { get; set; }

    /// <summary>
    /// <inheritdoc cref="ResourceHeaders.ContentType"/>
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// The length of the stored content in bytes, measured as it was written. Versions stored by releases before
    /// lengths were measured keep the length their caller declared.
    /// </summary>
    public long? ContentLength { get; set; }

    /// <summary>
    /// The SHA-256 of the content, as <c>sha256:</c> followed by lowercase hex, computed as it was written and stored
    /// with the version. <see langword="null"/> means not recorded yet (a version stored before hashes were kept, until
    /// the SQL Server provider's ContentHashBackfill reaches it), never a mismatch.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// The URI of a stored resource, from the URI provider each time the version is returned; <see langword="null"/>
    /// without one.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// The metadata of a stored resource.
    /// </summary>
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// The version this one was copied or restored from; <see langword="null"/> for a version that was uploaded, or
    /// stored by a provider that doesn't record it.
    /// </summary>
    public ResourceOrigin? CopiedFrom { get; set; }

    /// <summary>
    /// When the version expires and is deleted, within one interval, by <see cref="ResourcefullOptionsBuilder.UseExpiry"/>;
    /// <see langword="null"/> for a version that doesn't expire.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}