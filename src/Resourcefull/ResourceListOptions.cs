namespace Resourcefull;

/// <summary>
/// Which current resources a listing returns, and how many per page.
/// </summary>
public sealed class ResourceListOptions
{
    /// <summary>The most resources a page can hold.</summary>
    public const int MaxPageSize = 5000;

    /// <summary>
    /// Only resources whose names start with this, compared ordinally (case-sensitive), such as <c>2026/10/</c>.
    /// <see langword="null"/> or empty lists every resource.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>Resources per page, from 1 to <see cref="MaxPageSize"/>. The default is 1,000.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>
    /// Only resources whose current version has this metadata entry, such as <c>new("customer", "42")</c>; the key
    /// must be one <see cref="ResourcefullOptionsBuilder.UseMetadataSearch"/> named. <see langword="null"/> lists
    /// resources whatever their metadata.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    public ResourceMetadataMatch? MetadataMatch { get; init; }

    internal const int DefaultPageSize = 1000;
}

/// <summary>
/// A metadata entry a listing looks for: a key and its value, both compared exactly, ordinally (case-sensitive).
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Key">The metadata key, one that <see cref="ResourcefullOptionsBuilder.UseMetadataSearch"/> named.</param>
/// <param name="Value">The value the key must have, at most <see cref="MaxValueLength"/> characters.</param>
public sealed record ResourceMetadataMatch(string Key, string Value)
{
    /// <summary>The longest metadata key that can be searched.</summary>
    public const int MaxKeyLength = 128;

    /// <summary>The longest value a searchable key can have.</summary>
    public const int MaxValueLength = 256;

    /// <summary>Whether <paramref name="metadata"/> has this entry.</summary>
    /// <param name="metadata">A version's metadata.</param>
    public bool IsMatch(IReadOnlyDictionary<string, string> metadata) =>
        metadata.TryGetValue(Key, out string? value) && value == Value;
}

/// <summary>
/// One page of a listing, and where to continue: current resources ordered by name, or the versions in a bin, newest
/// deletion first.
/// </summary>
/// <param name="resources">The page's resources.</param>
/// <param name="continuationToken">Where the next page starts, or <see langword="null"/> when this is the last page.</param>
public sealed class ResourcePage(IReadOnlyList<ResourceInfo> resources, string? continuationToken)
{
    /// <summary>The page's resources, in the listing's order.</summary>
    public IReadOnlyList<ResourceInfo> Resources { get; } = resources;

    /// <summary>
    /// Pass this to the next call, with the same <see cref="ResourceListOptions.Prefix"/>, to get the next page;
    /// <see langword="null"/> when this is the last page. It's an opaque string of at most about 1 KB that's safe in a
    /// URL, so an API can hand it to its own callers.
    /// </summary>
    public string? ContinuationToken { get; } = continuationToken;
}
