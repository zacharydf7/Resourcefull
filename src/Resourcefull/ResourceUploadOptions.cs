namespace Resourcefull;

/// <summary>
/// Defines configuration options for uploading a resource.
/// </summary>
public class ResourceUploadOptions
{
    /// <summary>
    /// What the upload does when the name already has a current version: <see cref="ResourceWriteMode.Create"/>, the
    /// default, refuses it, and <see cref="ResourceWriteMode.Replace"/> stores the upload as the new current version.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public ResourceWriteMode Mode { get; init; }

    /// <summary>
    /// Store the upload only if the name's current version meets these, decided by the write itself; otherwise
    /// <see cref="ResourcePreconditionFailedException"/> is thrown and nothing is stored. They need
    /// <see cref="Mode"/> set to <see cref="ResourceWriteMode.Replace"/>, since a name that meets them has a
    /// current version.
    /// </summary>
    public ResourceConditions? Conditions { get; init; }

    /// <summary>
    /// <inheritdoc cref="ResourceHeaders(string?, long?)"/>
    /// </summary>
    public ResourceHeaders? Headers { get; init; }

    /// <summary>
    /// Describes metadata to store with the uploaded resource.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// When the uploaded version expires: after it, <see cref="ResourcefullOptionsBuilder.UseExpiry"/> deletes it within
    /// one interval, as a delete of that version would. Any time works, a past one included; <see langword="null"/>
    /// never expires.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Until when the uploaded version can't be deleted, by anyone or anything, expiry and pruning included; see
    /// <see cref="IResourceContainerClient.SetRetentionAsync"/>. <see langword="null"/> takes the container's
    /// <see cref="ContainerPolicy.RetainFor"/>, if it has one.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? RetainUntil { get; init; }

    /// <summary>
    /// Whether the uploaded version is stored under a legal hold, which keeps it from being deleted until the hold is
    /// lifted; see <see cref="IResourceContainerClient.SetLegalHoldAsync"/>.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public bool LegalHold { get; init; }
}