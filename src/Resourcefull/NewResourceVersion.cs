using System.Collections.ObjectModel;

namespace Resourcefull.Providers;

/// <summary>
/// A version for a <see cref="ResourceRepository"/> to store: where it goes, what its writer declared, and where it
/// came from. What the write measures as it stores the content (its length and hash) comes back in
/// <see cref="WrittenContent"/>, with the time the version became current.
/// <para>Coming in 4.0.</para>
/// </summary>
/// <param name="Location">The container and name the version is stored under.</param>
/// <param name="VersionId">The version's ID, which the clients choose.</param>
public sealed record NewResourceVersion(ResourceLocation Location, string VersionId)
{
    /// <summary>
    /// <inheritdoc cref="ResourceHeaders.ContentType"/>
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The content's length as its writer declared it; <see langword="null"/> when undeclared. A write whose content
    /// turns out a different length is refused.
    /// </summary>
    public long? DeclaredLength { get; init; }

    /// <summary>The version's metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>When the version expires; <see langword="null"/> for a version that doesn't.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The version a copy or restore was made from; <see langword="null"/> for an upload.</summary>
    public ResourceOrigin? CopiedFrom { get; init; }

    /// <summary>
    /// For an import only, when the version first became current in the store it came from, kept as its
    /// <see cref="ResourceInfo.LastModified"/>; <see langword="null"/> otherwise, as the write stamps its own time.
    /// </summary>
    public DateTimeOffset? LastModified { get; init; }
}
