using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Http")]
[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull;

/// <summary>
/// Defines a mechanism for retrieving a URI for a specified resource.
/// </summary>
public interface IUriProvider
{
    /// <summary>
    /// Retrieves the URI for the specified resource.
    /// </summary>
    /// <param name="context">The context used when generating the URI.</param>
    /// <returns>A <see cref="String"/> containing the URI to the specified resource.</returns>
    string? GetAbsoluteUri(ResourcefullContext context);
}
