namespace Resourcefull;

/// <summary>
/// Describes a stored version of a resource. It's immutable: to change a stored version's metadata or content type,
/// call <see cref="IResourceContainerClient.SetResourceMetadataAsync"/> or
/// <see cref="IResourceContainerClient.SetContentTypeAsync"/>.
/// </summary>
public sealed record ResourceInfo
{
    /// <summary>
    /// The container name of a stored resource.
    /// </summary>
    public required string ContainerName { get; init; }

    /// <summary>
    /// The unique name of a stored resource.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The version ID of a stored resource: an opaque, unique string. Compare it for equality only; its format is not
    /// part of the contract. Every stored version has one, so a provider sets it on each <see cref="ResourceInfo"/> it
    /// returns.
    /// </summary>
    public required string VersionId { get; init; }

    /// <summary>
    /// When the version became current, by the clock of the application server that stored it. Informational only:
    /// which version is current, and the order versions are listed in, are decided by the store.
    /// </summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>
    /// Indicates whether this is the current (most recently stored) version of the resource. When listing
    /// versions, exactly one entry has this set to <see langword="true"/>.
    /// </summary>
    public bool IsLatest { get; init; }

    /// <summary>
    /// <inheritdoc cref="ResourceHeaders.ContentType"/>
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The length of the stored content in bytes, measured as it was written. Versions stored by releases before
    /// lengths were measured keep the length their caller declared.
    /// </summary>
    public long? ContentLength { get; init; }

    /// <summary>
    /// The SHA-256 of the content, as <c>sha256:</c> followed by lowercase hex, computed as it was written and stored
    /// with the version. <see langword="null"/> means not recorded yet (a version stored before hashes were kept, until
    /// the SQL Server provider's ContentHashBackfill reaches it), never a mismatch.
    /// </summary>
    public string? ContentHash { get; init; }

    /// <summary>
    /// The URI of a stored resource, from the URI provider each time the version is returned; <see langword="null"/>
    /// without one.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// The metadata of a stored resource.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The version this one was copied or restored from; <see langword="null"/> for a version that was uploaded, or
    /// stored by a provider that doesn't record it.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public ResourceOrigin? CopiedFrom { get; init; }

    /// <summary>
    /// When the version expires and is deleted, within one interval, by <see cref="ResourcefullOptionsBuilder.UseExpiry"/>;
    /// <see langword="null"/> for a version that doesn't expire.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Until when the version can't be deleted, by anyone or anything, expiry and pruning included;
    /// <see langword="null"/> for a version without one. See <see cref="IResourceContainerClient.SetRetentionAsync"/>.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? RetainUntil { get; init; }

    /// <summary>
    /// Whether the version is under a legal hold, which keeps it from being deleted until the hold is lifted, whatever
    /// its <see cref="RetainUntil"/>. See <see cref="IResourceContainerClient.SetLegalHoldAsync"/>.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public bool LegalHold { get; init; }

    /// <summary>
    /// Whether the version may be deleted at <paramref name="now"/>: it's under no legal hold, and its
    /// <see cref="RetainUntil"/> is unset or has passed.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="now">The time to judge by.</param>
    public bool IsDeletableAt(DateTimeOffset now) => !LegalHold && !(RetainUntil > now);

    /// <summary>
    /// When the version was deleted into the bin, for a version that
    /// <see cref="IResourceContainerClient.ListDeletedAsync"/> lists; <see langword="null"/> for every other version.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>
    /// When the version in the bin is purged for good, within one interval, by
    /// <see cref="ResourcefullOptionsBuilder.UseExpiry"/>: its <see cref="DeletedAt"/> plus the
    /// <see cref="ResourceContainerPolicy.KeepDeletedFor"/> in force when it was deleted. <see langword="null"/> for a
    /// version that isn't in the bin.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? PurgeAt { get; init; }
}