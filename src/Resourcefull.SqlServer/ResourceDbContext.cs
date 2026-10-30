using Microsoft.EntityFrameworkCore;
using Resourcefull.SqlServer.Models;
using System.Reflection;

namespace Resourcefull.SqlServer;

internal class ResourceDbContext(DbContextOptions<ResourceDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Binary collation for names and version IDs: case-sensitive ordinal comparison, the same on every server
    /// regardless of its default collation and the same as the in-memory provider.
    /// </summary>
    internal const string NameCollation = "Latin1_General_100_BIN2";

    /// <summary>The last migration named before <see cref="IsResourcefullMigration"/> could tell them apart.</summary>
    internal const string LastUnmarkedMigration = "20261030011747_AddExpiresAt";

    /// <summary>
    /// Whether a migration ID is named as Resourcefull's: every migration after <see cref="LastUnmarkedMigration"/>
    /// starts its name with <c>Resourcefull_</c> (<c>dotnet ef migrations add Resourcefull_AddThing</c>), so a
    /// migration history an application's own EF Core migrations share tells them apart.
    /// </summary>
    internal static bool IsResourcefullMigration(string migrationId) =>
        migrationId.AsSpan(migrationId.IndexOf('_') + 1).StartsWith("Resourcefull_", StringComparison.Ordinal);

    internal DbSet<ResourceModel> Resources { get; set; }
    internal DbSet<ResourceChunkModel> ResourceChunks { get; set; }
    internal DbSet<ResourceContainerModel> ResourceContainers { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}