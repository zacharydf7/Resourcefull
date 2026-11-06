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
        builder.HasIndex(r => new { r.ExpiresAt, r.VersionId, r.ContainerName, r.Name })
            .HasFilter("\"ExpiresAt\" IS NOT NULL")
            .HasDatabaseName("IX_Resources_ExpiresAt");

        // Only the versions that are retained or held, so a container delete finds whether it may go with a seek.
        builder.HasIndex(r => new { r.ContainerName, r.RetainUntil })
            .HasFilter("\"RetainUntil\" IS NOT NULL")
            .HasDatabaseName("IX_Resources_RetainUntil");
        builder.HasIndex(r => r.ContainerName)
            .HasFilter("\"LegalHold\" = 1")
            .HasDatabaseName("IX_Resources_LegalHold");

        // A version an upload is still storing, or one in the bin, is left out of every query, so nothing sees it; only
        // the upload, the bin's own queries and the deletes, of a container or of abandoned uploads, ask for it with
        // IgnoreQueryFilters. Reads of a name's current version skip the filter too, since a current version is never
        // staged or in the bin.
        builder.HasQueryFilter(r => r.StagedAt == null && r.DeletedAt == null);

        // Only the versions in bins, newest deletion first in each container, as the bin lists them.
        builder.HasIndex(r => new { r.ContainerName, r.DeletedAt, r.VersionId, r.Name })
            .IsDescending(false, true, false, false)
            .HasFilter("\"DeletedAt\" IS NOT NULL")
            .HasDatabaseName("IX_Resources_DeletedAt");

        // Only the versions in bins, in the order the sweep purges them, so a sweep that finds none due reads nothing else.
        builder.HasIndex(r => new { r.PurgeAt, r.VersionId, r.ContainerName, r.Name })
            .HasFilter("\"PurgeAt\" IS NOT NULL")
            .HasDatabaseName("IX_Resources_PurgeAt");

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
