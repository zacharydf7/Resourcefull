namespace Resourcefull;

/// <summary>
/// Which containers a listing returns, and how many per page.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ContainerListOptions
{
    /// <summary>The most containers a page can hold.</summary>
    public const int MaxPageSize = 5000;

    /// <summary>
    /// Only containers whose names start with this, compared ordinally (case-sensitive), such as <c>user-</c>.
    /// <see langword="null"/> or empty lists every container.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>Containers per page, from 1 to <see cref="MaxPageSize"/>. The default is 1,000.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    internal const int DefaultPageSize = 1000;
}

/// <summary>
/// One page of a container listing: containers ordered by name, and where to continue.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="containers">The page's containers.</param>
/// <param name="continuationToken">Where the next page starts, or <see langword="null"/> when this is the last page.</param>
public sealed class ContainerPage(IReadOnlyList<ContainerInfo> containers, string? continuationToken)
{
    /// <summary>The page's containers, ordered by name.</summary>
    public IReadOnlyList<ContainerInfo> Containers { get; } = containers;

    /// <summary>
    /// Pass this to the next call, with the same <see cref="ContainerListOptions.Prefix"/>, to get the next page;
    /// <see langword="null"/> when this is the last page. It's an opaque string of at most about 1 KB that's safe in a
    /// URL, so an API can hand it to its own callers.
    /// </summary>
    public string? ContinuationToken { get; } = continuationToken;
}
