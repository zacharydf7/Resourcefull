namespace Resourcefull.PostgreSql.Models;

/// <summary>
/// One searchable metadata entry of a name's current version, kept so a listing by metadata seeks to its matches.
/// </summary>
internal class ResourceMetadataModel
{
    public long ResourceId { get; set; }
    public required string ContainerName { get; set; }
    public required string Name { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}
