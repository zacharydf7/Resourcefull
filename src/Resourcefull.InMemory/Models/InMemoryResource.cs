using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Resourcefull.Tests.Unit")]

namespace Resourcefull.InMemory.Models;

internal class InMemoryResource
{
    public required ResourceInfo ResourceInfo { get; init; }

    /// <summary>The stored bytes. Never changed after the version is stored, so readers can share it.</summary>
    public required byte[] Content { get; init; }

    /// <summary>The order this version was stored in; the highest for a name is its current version.</summary>
    public long Sequence { get; init; }
}
