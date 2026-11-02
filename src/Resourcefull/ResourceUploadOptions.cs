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
    public ResourceWriteMode Mode { get; set; }

    /// <summary>
    /// Store the upload only if the name's current version meets these, decided by the write itself; otherwise
    /// <see cref="ResourcePreconditionFailedException"/> is thrown and nothing is stored. They need
    /// <see cref="Mode"/> set to <see cref="ResourceWriteMode.Replace"/>, since a name that meets them has a
    /// current version.
    /// </summary>
    public ResourceConditions? Conditions { get; set; }

    /// <summary>
    /// <inheritdoc cref="ResourceHeaders(string?, long?)"/>
    /// </summary>
    public ResourceHeaders? Headers { get; set; }

    /// <summary>
    /// Describes metadata to store with the uploaded resource.
    /// </summary>
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

    /// <summary>
    /// When the uploaded version expires: after it, <see cref="ResourcefullOptionsBuilder.UseExpiry"/> deletes it within
    /// one interval, as a delete of that version would. Any time works, a past one included; <see langword="null"/>
    /// never expires.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}