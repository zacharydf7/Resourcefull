namespace Resourcefull.InMemory.Models;
internal class InMemoryResourceContainer
{
    public required string Name { get; set; }
    public List<InMemoryResource> Resources { get; set; } = [];
}
