using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer.Configuration;
internal class ResourceChunkConfiguration : IEntityTypeConfiguration<ResourceChunkModel>
{
    public void Configure(EntityTypeBuilder<ResourceChunkModel> builder)
    {
        builder.HasKey(c => new { c.ResourceId, c.Index });
    }
}
