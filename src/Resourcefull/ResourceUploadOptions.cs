namespace Resourcefull;

/// <summary>
/// Defines configuration options for uploading a resource.
/// </summary>
public class ResourceUploadOptions
{
    /// <summary>
    /// Determines whether to overwrite a resource when a resource with the same name exists. Applies only when
    /// versioning is disabled; with versioning enabled every upload adds a version.
    /// </summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// Store the upload only if no resource with this name exists yet, whether or not versioning is enabled. The
    /// check is part of the write, so of several concurrent create-only uploads of one name exactly one succeeds and
    /// the rest throw <see cref="ResourceConflictException"/>. Takes precedence over <see cref="Overwrite"/>.
    /// </summary>
    public bool CreateOnly { get; set; }

    /// <summary>
    /// Store the upload only if the name's current version meets these, decided by the write itself; otherwise
    /// <see cref="ResourcePreconditionFailedException"/> is thrown and nothing is stored. An upload that meets them
    /// replaces the current version, as if <see cref="Overwrite"/> were set. Can't be combined with
    /// <see cref="CreateOnly"/>.
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
    /// <para>Coming in 3.2.</para>
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}