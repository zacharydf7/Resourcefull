using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Resourcefull.Sqlite.Models;

namespace Resourcefull.Sqlite.Configuration;
internal class ResourceChunkConfiguration : IEntityTypeConfiguration<ResourceChunkModel>
{
    public void Configure(EntityTypeBuilder<ResourceChunkModel> builder)
    {
        builder.HasKey(c => new { c.ResourceId, c.Index });
    }
}
