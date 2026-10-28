namespace Resourcefull.InMemory.Models;

internal class InMemoryResourceContainer
{
    public required string Name { get; init; }

    /// <summary>Every stored version of each resource, keyed by resource name; a name with no versions has no entry.</summary>
    public Dictionary<string, List<InMemoryResource>> Resources { get; } = new(StringComparer.Ordinal);
}
