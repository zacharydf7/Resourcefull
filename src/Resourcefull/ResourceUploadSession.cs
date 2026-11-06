namespace Resourcefull;

/// <summary>
/// An upload session: a version whose content arrives over several appends, possibly from different processes, and
/// becomes current only when the session is completed.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed record ResourceUploadSession
{
    /// <summary>The container the version is stored in.</summary>
    public required string ContainerName { get; init; }

    /// <summary>The name the version is stored under.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The session's ID, given to every later call about it. Treat it as opaque.</summary>
    public required string UploadId { get; init; }

    /// <summary>How many bytes are stored so far: where the next append starts.</summary>
    public required long Offset { get; init; }

    /// <summary>
    /// The length the session said the version would have, if it did: the session completes only with that many bytes.
    /// </summary>
    public long? DeclaredLength { get; init; }
}

/// <summary>
/// What completing an upload session does when the name already has a current version.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceCompleteUploadOptions
{
    /// <summary>
    /// What the completion does when the name already has a current version: <see cref="ResourceWriteMode.Create"/>, the
    /// default, refuses it, and <see cref="ResourceWriteMode.Replace"/> stores the session's version as the new current
    /// version.
    /// </summary>
    public ResourceWriteMode Mode { get; init; }

    /// <summary>
    /// Complete the session only if the name's current version meets these, decided as the session's version becomes
    /// current; otherwise <see cref="ResourcePreconditionFailedException"/> is thrown and the session stays. They need
    /// <see cref="Mode"/> set to <see cref="ResourceWriteMode.Replace"/>, since a name that meets them has a current version.
    /// </summary>
    public ResourceConditions? Conditions { get; init; }
}

/// <summary>
/// What an upload session stores its version with: the content type, declared length, metadata and expiry, given once
/// when the session starts.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourceUploadSessionOptions
{
    /// <summary>
    /// The version's content type, and the length it will have. The length is checked against the container's limits at
    /// the start, so an upload that can't fit is refused before any content is sent. It's checked again as the session
    /// completes, which throws <see cref="ResourceContentLengthMismatchException"/> if a different length was appended.
    /// </summary>
    public ResourceHeaders? Headers { get; init; }

    /// <summary>The metadata to store with the version.</summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// When the version expires, once the session completes: as for <see cref="ResourceUploadOptions.ExpiresAt"/>.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Until when the version can't be deleted, once the session completes: as for
    /// <see cref="ResourceUploadOptions.RetainUntil"/>, counting <see cref="ContainerPolicy.RetainFor"/> from
    /// when the session starts.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? RetainUntil { get; init; }

    /// <summary>
    /// Whether the version is stored under a legal hold, once the session completes: as for
    /// <see cref="ResourceUploadOptions.LegalHold"/>.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public bool LegalHold { get; init; }
}
