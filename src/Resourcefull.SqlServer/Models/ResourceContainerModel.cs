namespace Resourcefull.SqlServer.Models;
internal class ResourceContainerModel
{
    public required string Name { get; set; }

    public string? Uri { get; set; }
    public IDictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

    // Navigation Properties
    public virtual List<ResourceModel>? Resources { get; set; }
}
