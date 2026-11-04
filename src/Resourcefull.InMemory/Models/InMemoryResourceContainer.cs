namespace Resourcefull.InMemory.Models;

internal class InMemoryResourceContainer
{
    public required string Name { get; init; }
    public required IDictionary<string, string> Metadata { get; init; }

    /// <summary>Every stored version of each resource, keyed by resource name; a name with no versions has no entry.</summary>
    public Dictionary<string, List<InMemoryResource>> Resources { get; } = new(StringComparer.Ordinal);

    /// <summary>The versions in the bin, keyed by resource name; a name with none there has no entry.</summary>
    public Dictionary<string, List<InMemoryResource>> Deleted { get; } = new(StringComparer.Ordinal);

    /// <summary>The versions upload sessions are storing, keyed by resource name and version ID.</summary>
    public Dictionary<(string Name, string VersionId), InMemoryStagedVersion> Staged { get; } = [];
}

/// <summary>A version an upload session is storing: what it was staged with, and the content appended so far.</summary>
internal class InMemoryStagedVersion
{
    public required Providers.NewResourceVersion Version { get; init; }

    public MemoryStream Content { get; } = new();

    /// <summary>When the session last stored anything, for the sweep of abandoned uploads.</summary>
    public DateTimeOffset StagedAt { get; set; }
}
