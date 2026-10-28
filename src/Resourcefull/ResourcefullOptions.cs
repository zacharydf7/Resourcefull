namespace Resourcefull;

public class ResourcefullOptions
{
    /// <summary>
    /// Gets or sets whether versioning is enabled.
    /// </summary>
    public bool IsVersioningEnabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of versions to keep.
    /// </summary>
    public int MaxVersions { get; set; }
}