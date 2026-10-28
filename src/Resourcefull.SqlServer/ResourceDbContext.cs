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

    internal DbSet<ResourceModel> Resources { get; set; }
    internal DbSet<ResourceChunkModel> ResourceChunks { get; set; }
    internal DbSet<ResourceContainerModel> ResourceContainers { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}