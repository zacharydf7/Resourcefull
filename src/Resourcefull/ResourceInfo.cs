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
    /// The length of the stored content in bytes, measured as it was written. Versions stored by releases before
    /// lengths were measured keep the length their caller declared.
    /// </summary>
    public long? ContentLength { get; set; }

    /// <summary>
    /// The SHA-256 of the content, as <c>sha256:</c> followed by lowercase hex, computed as it was written. Set on the
    /// <see cref="ResourceInfo"/> an upload or restore returns; the hash is not stored with the resource, so reads
    /// return <see langword="null"/>.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// The URI of a stored resource.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// The metadata of a stored resource.
    /// </summary>
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
}