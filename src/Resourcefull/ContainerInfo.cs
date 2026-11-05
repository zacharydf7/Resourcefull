namespace Resourcefull;

/// <summary>
/// A container's attributes, as stored when they were read. It doesn't change, and changing a copy (with
/// <see langword="with"/>) changes nothing that's stored: use <see cref="IResourceContainerClient.SetMetadataAsync"/> to
/// change a container's metadata. Two reads of the same container are equal: metadata is compared by its entries.
/// </summary>
public sealed record ContainerInfo
{
    private readonly MetadataDictionary _metadata = MetadataDictionary.Empty;

    /// <summary>
    /// The name of the resource container.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The URI of the resource container, from the URI provider each time the container is returned;
    /// <see langword="null"/> without one.
    /// </summary>
    public string? Uri { get; init; }

    /// <summary>
    /// The metadata associated with the resource container.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata
    {
        get => _metadata;
        init => _metadata = MetadataDictionary.From(value);
    }
}
