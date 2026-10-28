namespace Resourcefull;

/// <summary>
/// Defines configuration options for uploading a resource.
/// </summary>
public class ResourceUploadOptions
{
    /// <summary>
    /// Determines whether to overwrite a resource when a resource with the same name exists.
    /// </summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// <inheritdoc cref="ResourceHeaders(string?, long?)"/>
    /// </summary>
    public ResourceHeaders? Headers { get; set; }

    /// <summary>
    /// Describes metadata to store with the uploaded resource.
    /// </summary>
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
}