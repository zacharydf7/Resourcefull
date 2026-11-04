namespace Resourcefull;

/// <summary>
/// The options Resourcefull runs with, as configured through <see cref="ResourcefullOptionsBuilder"/>. Inject
/// <c>IOptions&lt;ResourcefullOptions&gt;</c> to read them, or for a named store,
/// <c>IOptionsMonitor&lt;ResourcefullOptions&gt;</c> and call <c>Get</c> with its name.
/// </summary>
public class ResourcefullOptions
{
    /// <summary>
    /// Gets how many versions of each name to keep, including the current one, in a container whose
    /// <see cref="ResourceContainerPolicy.MaxVersions"/> isn't set. Set through
    /// <see cref="ResourcefullOptionsBuilder.UseVersioning(int?)"/>; 1 without it, which keeps only the current version.
    /// </summary>
    public int MaxVersions { get; internal set; } = 1;

    /// <summary>
    /// Gets what telemetry records. Set through
    /// <see cref="ResourcefullOptionsBuilder.UseTelemetry(Action{ResourcefullTelemetryOptions})"/>.
    /// </summary>
    public ResourcefullTelemetryOptions Telemetry { get; internal set; } = new();

    /// <summary>
    /// Gets the metadata keys a listing can search for, ordinally. Set through
    /// <see cref="ResourcefullOptionsBuilder.UseMetadataSearch"/>; empty without it.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public IReadOnlySet<string> SearchableMetadataKeys => _searchableMetadataKeys;

    internal readonly HashSet<string> _searchableMetadataKeys = new(StringComparer.Ordinal);

    /// <summary>The store's name, recorded on its telemetry; null for the store without one.</summary>
    internal string? StoreName { get; set; }
}
