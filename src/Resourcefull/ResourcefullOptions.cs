namespace Resourcefull;

/// <summary>
/// The options Resourcefull runs with, as configured through <see cref="ResourcefullOptionsBuilder"/>. Inject
/// <c>IOptions&lt;ResourcefullOptions&gt;</c> to read them.
/// </summary>
public class ResourcefullOptions
{
    /// <summary>
    /// Gets whether versioning is enabled. Set through <see cref="ResourcefullOptionsBuilder.UseVersioning(int?)"/>.
    /// </summary>
    public bool IsVersioningEnabled { get; internal set; }

    /// <summary>
    /// Gets the maximum number of versions to keep. Set through <see cref="ResourcefullOptionsBuilder.UseVersioning(int?)"/>,
    /// which guarantees it is greater than zero whenever versioning is enabled.
    /// </summary>
    public int MaxVersions { get; internal set; }
}
