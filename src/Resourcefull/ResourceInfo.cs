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
    /// When the version was written, by the clock of the application server that wrote it. Informational only: which
    /// version is current, and the order versions are listed in, are decided by the store.
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
    /// <inheritdoc cref="ResourceHeaders.ContentLength"/>
    /// </summary>
    public long? ContentLength { get; set; }

    /// <summary>
    /// The URI of a stored resource.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// The metadata of a stored resource.
    /// </summary>
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
}