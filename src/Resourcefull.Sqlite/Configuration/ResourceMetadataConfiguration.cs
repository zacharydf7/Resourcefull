using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Resourcefull.Sqlite.Models;

namespace Resourcefull.Sqlite.Configuration;

internal class ResourceMetadataConfiguration : IEntityTypeConfiguration<ResourceMetadataModel>
{
    public void Configure(EntityTypeBuilder<ResourceMetadataModel> builder)
    {
        builder.ToTable("ResourceMetadata");
        builder.HasKey(m => new { m.ResourceId, m.Key });
        // Rows go with their version, whichever way it's deleted.
        builder.HasOne<ResourceModel>().WithMany().HasForeignKey(m => m.ResourceId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(m => m.ContainerName)
            .HasMaxLength(ResourceNames.MaxContainerNameLength)
            .UseCollation(ResourceDbContext.NameCollation);
        builder.Property(m => m.Name)
            .HasMaxLength(ResourceNames.MaxResourceNameLength)
            .UseCollation(ResourceDbContext.NameCollation);
        builder.Property(m => m.Key)
            .HasMaxLength(ResourceMetadataMatch.MaxKeyLength)
            .UseCollation(ResourceDbContext.NameCollation);
        builder.Property(m => m.Value)
            .HasMaxLength(ResourceMetadataMatch.MaxValueLength)
            .UseCollation(ResourceDbContext.NameCollation);

        // A search seeks to the container, key and value, and reads its page in name order.
        builder.HasIndex(m => new { m.ContainerName, m.Key, m.Value, m.Name })
            .HasDatabaseName("IX_ResourceMetadata_Search");
    }
}
