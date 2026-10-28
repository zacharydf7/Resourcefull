using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull.InMemory.Models;

internal class InMemoryResource
{
    public required string ContainerName { get; set; }
    public string Name { get; set; } = string.Empty;
    public ResourceInfo ResourceInfo { get; set; } = null!;
    public Stream Stream { get; set; } = null!;

    /// <summary>The order this version was stored in; the highest for a name is its current version.</summary>
    public long Sequence { get; init; }

}