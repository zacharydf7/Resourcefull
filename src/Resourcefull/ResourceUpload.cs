namespace Resourcefull;

/// <summary>
/// An upload session: a version whose content arrives over several appends, possibly from different processes, and
/// becomes current only when the session is completed.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="ContainerName">The container the version is stored in.</param>
/// <param name="ResourceName">The name the version is stored under.</param>
/// <param name="UploadId">The session's ID, given to every later call about it. Treat it as opaque.</param>
/// <param name="Offset">How many bytes are stored so far: where the next append starts.</param>
/// <param name="DeclaredLength">
/// The length the session said the version would have, if it did: the session completes only with that many bytes.
/// </param>
public sealed record ResourceUpload(string ContainerName, string ResourceName, string UploadId, long Offset,
    long? DeclaredLength = null);

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
    /// <see cref="ResourceUploadOptions.RetainUntil"/>, counting <see cref="ResourceContainerPolicy.RetainFor"/> from
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
