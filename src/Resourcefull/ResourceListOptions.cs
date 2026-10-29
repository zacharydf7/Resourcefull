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
    public int PageSize { get; init; } = 1000;
}

/// <summary>
/// One page of a listing: current resources ordered by name, and where to continue.
/// </summary>
/// <param name="resources">The page's resources.</param>
/// <param name="continuationToken">Where the next page starts, or <see langword="null"/> when this is the last page.</param>
public sealed class ResourcePage(IReadOnlyList<ResourceInfo> resources, string? continuationToken)
{
    /// <summary>The page's resources, ordered by name.</summary>
    public IReadOnlyList<ResourceInfo> Resources { get; } = resources;

    /// <summary>
    /// Pass this to the next call, with the same <see cref="ResourceListOptions.Prefix"/>, to get the next page;
    /// <see langword="null"/> when this is the last page. It's an opaque string of at most about 1 KB that's safe in a
    /// URL, so an API can hand it to its own callers.
    /// </summary>
    public string? ContinuationToken { get; } = continuationToken;
}
