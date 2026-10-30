namespace Resourcefull.SqlServer.Models;

internal class ResourceModel
{
    public long Id { get; set; }
    public required string ContainerName { get; set; }
    public required string Name { get; set; }
    public string? VersionId { get; set; }
    public DateTimeOffset LastModified { get; set; }
    public bool IsLatest { get; set; }
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
    public string? ContentHash { get; set; }
    public string? Uri { get; set; }
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    public string? CopiedFromContainerName { get; set; }
    public string? CopiedFromName { get; set; }
    public string? CopiedFromVersionId { get; set; }

    // Navigation properties
    public virtual ResourceContainerModel? ResourceContainer { get; set; }
    public virtual ICollection<ResourceChunkModel>? Chunks { get; set; }
}
