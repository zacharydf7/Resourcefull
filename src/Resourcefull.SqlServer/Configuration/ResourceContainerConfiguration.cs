using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Resourcefull.SqlServer.Converters;
using Resourcefull.SqlServer.Models;

namespace Resourcefull.SqlServer.Configuration;
internal class ResourceContainerConfiguration : IEntityTypeConfiguration<ResourceContainerModel>
{
    public void Configure(EntityTypeBuilder<ResourceContainerModel> builder)
    {
        builder.HasKey(rc => rc.Name);

        builder.Property(rc => rc.Name)
            .IsRequired();

        builder.Property(rc => rc.Metadata)
            .HasColumnType("varchar(MAX)")
            .HasConversion<MetadataConverter>();

        builder.HasMany(rc => rc.Resources)
            .WithOne(r => r.ResourceContainer)
            .HasForeignKey(r => r.ContainerName);
    }
}
