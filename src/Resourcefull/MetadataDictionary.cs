using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Resourcefull;

/// <summary>
/// The metadata of a <see cref="ResourceInfo"/> or <see cref="ContainerInfo"/>: a read-only dictionary that equals
/// another with the same entries, so two reads of the same version or container are equal.
/// </summary>
internal sealed class MetadataDictionary : IReadOnlyDictionary<string, string>, IEquatable<MetadataDictionary>
{
    public static readonly MetadataDictionary Empty = new(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _entries;

    private MetadataDictionary(IReadOnlyDictionary<string, string> entries) => _entries = entries;

    /// <summary>A copy of <paramref name="entries"/>, so changing the dictionary given changes nothing here.</summary>
    public static MetadataDictionary From(IReadOnlyDictionary<string, string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries switch
        {
            MetadataDictionary metadata => metadata,
            { Count: 0 } => Empty,
            _ => new MetadataDictionary(entries.ToDictionary(StringComparer.Ordinal))
        };
    }

    public string this[string key] => _entries[key];

    public IEnumerable<string> Keys => _entries.Keys;

    public IEnumerable<string> Values => _entries.Values;

    public int Count => _entries.Count;

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _entries.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(MetadataDictionary? other) =>
        other is not null && (ReferenceEquals(this, other) || Count == other.Count
            && this.All(entry => other.TryGetValue(entry.Key, out string? value) && string.Equals(entry.Value, value, StringComparison.Ordinal)));

    public override bool Equals(object? obj) => Equals(obj as MetadataDictionary);

    // Adds the entries' hashes, so the order the entries are kept in doesn't matter.
    public override int GetHashCode() => this.Aggregate(Count, (hash, entry) => unchecked(hash + HashCode.Combine(entry.Key, entry.Value)));
}
