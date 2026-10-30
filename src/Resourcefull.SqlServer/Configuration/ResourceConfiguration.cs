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
            .IsUnique()
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

        // "sha256:" and 64 hex digits. Null for versions stored before hashes were kept, until ContentHashBackfill runs.
        builder.Property(r => r.ContentHash)
            .HasMaxLength(71)
            .IsUnicode(false);

        builder.Property(r => r.VersionId)
            .HasColumnName("VersionId")
            .IsRequired()
            .HasMaxLength(ResourceNames.MaxVersionIdLength)
            .UseCollation(ResourceDbContext.NameCollation)
            .HasValueGenerator<VersionIdValueGenerator>();

        // The version a copy or restore came from: a record, not a foreign key, so it outlives the source.
        builder.Property(r => r.CopiedFromContainerName)
            .HasMaxLength(ResourceNames.MaxContainerNameLength)
            .UseCollation(ResourceDbContext.NameCollation);

        builder.Property(r => r.CopiedFromName)
            .HasMaxLength(ResourceNames.MaxResourceNameLength)
            .UseCollation(ResourceDbContext.NameCollation);

        builder.Property(r => r.CopiedFromVersionId)
            .HasMaxLength(ResourceNames.MaxVersionIdLength)
            .UseCollation(ResourceDbContext.NameCollation);

        // Only the versions that expire, in the order a sweep lists them, so each batch is a seek however many versions
        // there are, expired ones that read-only containers keep included.
        builder.HasIndex(r => new { r.ExpiresAt, r.VersionId })
            .HasFilter("[ExpiresAt] IS NOT NULL")
            .HasDatabaseName("IX_Resources_ExpiresAt");

        builder.HasMany(r => r.Chunks)
            .WithOne()
            .HasForeignKey(c => c.ResourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ResourceContainer)
            .WithMany(rc => rc.Resources)
            .HasForeignKey(r => r.ContainerName);
    }
}
