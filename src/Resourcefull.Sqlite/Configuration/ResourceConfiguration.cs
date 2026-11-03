using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Resourcefull.Sqlite.Models;

namespace Resourcefull.Sqlite.Configuration;
internal class ResourceConfiguration : IEntityTypeConfiguration<ResourceModel>
{
    public void Configure(EntityTypeBuilder<ResourceModel> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .ValueGeneratedOnAdd();

        builder.HasIndex(r => new { r.ContainerName, r.Name, r.VersionId })
            .IsUnique()
            .HasDatabaseName("UX_Resources_Container_Name_Version");

        builder.HasIndex(r => new { r.ContainerName, r.Name })
            .IsUnique()
            .HasFilter("\"IsLatest\"")
            .HasDatabaseName("IX_Resources_Latest");

        builder.Property(r => r.ContainerName)
            .HasMaxLength(ResourceNames.MaxContainerNameLength)
            .UseCollation(ResourceDbContext.NameCollation);

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(ResourceNames.MaxResourceNameLength)
            .UseCollation(ResourceDbContext.NameCollation);

        builder.Property(r => r.Metadata)
            .HasColumnType("text")
            .HasConversion<MetadataConverter>();

        // "sha256:" and 64 hex digits.
        builder.Property(r => r.ContentHash)
            .HasMaxLength(71);

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
            .HasFilter("\"ExpiresAt\" IS NOT NULL")
            .HasDatabaseName("IX_Resources_ExpiresAt");

        // A version an upload is still storing is left out of every query, so nothing sees it until it's complete; only
        // the upload and the deletes, of a container or of abandoned uploads, ask for it with IgnoreQueryFilters. Reads
        // of a name's current version skip the filter too, since a current version is never staged.
        builder.HasQueryFilter(r => r.StagedAt == null);

        // Only the versions being stored, so the sweep for abandoned uploads reads nothing else; SQLite's indexes hold
        // each row's ID, so it can find them from the index alone.
        builder.HasIndex(r => r.StagedAt)
            .HasFilter("\"StagedAt\" IS NOT NULL")
            .HasDatabaseName("IX_Resources_StagedAt");

        builder.HasMany(r => r.Chunks)
            .WithOne()
            .HasForeignKey(c => c.ResourceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.ResourceContainer)
            .WithMany(rc => rc.Resources)
            .HasForeignKey(r => r.ContainerName);
    }
}
