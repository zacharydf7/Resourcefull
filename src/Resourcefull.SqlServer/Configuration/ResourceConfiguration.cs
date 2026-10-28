using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Resourcefull.SqlServer.Converters;
using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.ValueGenerators;

namespace Resourcefull.SqlServer.Configuration;
internal class ResourceConfiguration : IEntityTypeConfiguration<ResourceModel>
{
    public void Configure(EntityTypeBuilder<ResourceModel> builder)
    {
        builder.HasKey(r => r.Id)
            .IsClustered(false);

        builder.Property(r => r.Id)
            .ValueGeneratedOnAdd();

        builder.HasIndex(r => new { r.ContainerName, r.Name, r.VersionId })
            .IsUnique()
            .IsClustered()
            .HasDatabaseName("UX_Resources_Container_Name_Version");

        builder.HasIndex(r => new { r.ContainerName, r.Name })
            .HasFilter("[IsLatest] = 1")
            .HasDatabaseName("IX_Resources_Latest");

        builder.Property(r => r.ContainerName)
            .HasMaxLength(ResourceNames.MaxContainerNameLength)
            .UseCollation(ResourceDbContext.NameCollation);

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(ResourceNames.MaxResourceNameLength)
            .UseCollation(ResourceDbContext.NameCollation);

        builder.Property(r => r.Metadata)
            .HasColumnType("varchar(MAX)")
            .HasConversion<MetadataConverter>();

        builder.Property(r => r.VersionId)
            .HasColumnName("VersionId")
            .IsRequired()
            .HasMaxLength(ResourceNames.MaxVersionIdLength)
            .UseCollation(ResourceDbContext.NameCollation)
            .HasValueGenerator<VersionIdValueGenerator>();

        builder.HasMany(r => r.Chunks)
            .WithOne()
            .HasForeignKey(c => c.ResourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ResourceContainer)
            .WithMany(rc => rc.Resources)
            .HasForeignKey(r => r.ContainerName);
    }
}
