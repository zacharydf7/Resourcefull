namespace Resourcefull.PostgreSql.Models;

internal class ResourceChunkModel
{
    public long ResourceId { get; set; }
    public int Index { get; set; }
    public byte[] Data { get; set; } = null!;
}
