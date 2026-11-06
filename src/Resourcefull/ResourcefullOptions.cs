namespace Resourcefull;

/// <summary>
/// The options Resourcefull runs with, as configured through <see cref="ResourcefullOptionsBuilder"/>. Inject
/// <c>IOptions&lt;ResourcefullOptions&gt;</c> to read them, or for a named store,
/// <c>IOptionsMonitor&lt;ResourcefullOptions&gt;</c> and call <c>Get</c> with its name.
/// </summary>
public sealed class ResourcefullOptions
{
    /// <summary>
    /// Gets how many versions of each name to keep, including the current one, in a container whose
    /// <see cref="ContainerPolicy.MaxVersions"/> isn't set. Set through
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
    /// </summary>
    public IReadOnlySet<string> SearchableMetadataKeys => _searchableMetadataKeys;

    /// <summary>
    /// Throws when <paramref name="metadata"/> gives one of <see cref="SearchableMetadataKeys"/> a value longer than an
    /// index can hold.
    /// </summary>
    internal void ThrowIfUnsearchable(IReadOnlyDictionary<string, string> metadata, string paramName)
    {
        foreach (string key in _searchableMetadataKeys)
        {
            if (metadata.TryGetValue(key, out string? value) && value.Length > ResourceMetadataMatch.MaxValueLength)
            {
                throw new ArgumentException($"The metadata value of {key} is {value.Length} characters long; a searchable " +
                    $"key's value can be at most {ResourceMetadataMatch.MaxValueLength}.", paramName);
            }
        }
    }

    internal readonly HashSet<string> _searchableMetadataKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets how long the expiry sweep keeps an upload session that nothing is appended to before deleting it: 24 hours
    /// with <see cref="ResourcefullOptionsBuilder.UseExpiry"/>, and <see langword="null"/> without it, since nothing then
    /// deletes such sessions.
    /// </summary>
    public TimeSpan? AbandonedUploadsDeletedAfter { get; internal set; }

    /// <summary>The store's name, recorded on its telemetry; null for the store without one.</summary>
    internal string? StoreName { get; set; }
}
