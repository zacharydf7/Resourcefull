using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Resourcefull.PostgreSql.Converters;
using Resourcefull.PostgreSql.Models;

namespace Resourcefull.PostgreSql.Configuration;
internal class ResourceContainerConfiguration : IEntityTypeConfiguration<ResourceContainerModel>
{
    public void Configure(EntityTypeBuilder<ResourceContainerModel> builder)
    {
        builder.HasKey(rc => rc.Name);

        builder.Property(rc => rc.Name)
            .IsRequired()
            .HasMaxLength(ResourceNames.MaxContainerNameLength)
            .UseCollation(ResourceDbContext.NameCollation);

        builder.Property(rc => rc.Metadata)
            .HasColumnType("text")
            .HasConversion<MetadataConverter>();

        builder.HasMany(rc => rc.Resources)
            .WithOne(r => r.ResourceContainer)
            .HasForeignKey(r => r.ContainerName);
    }
}
