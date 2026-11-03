using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Resourcefull.Sqlite.Models;
using System.Reflection;

namespace Resourcefull.Sqlite;

internal class ResourceDbContext(DbContextOptions<ResourceDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Byte-order collation for names and version IDs, SQLite's default: case-sensitive, and ordered by Unicode code
    /// point, which differs from .NET's ordinal order only between characters beyond U+FFFF and those from U+E000 to
    /// U+FFFF.
    /// </summary>
    internal const string NameCollation = "BINARY";

    /// <summary>
    /// Whether a migration ID is named as Resourcefull's: every migration's name starts with <c>Resourcefull_</c>
    /// (<c>dotnet ef migrations add Resourcefull_AddThing</c>), so a migration history an application's own EF Core
    /// migrations share tells them apart.
    /// </summary>
    internal static bool IsResourcefullMigration(string migrationId) =>
        migrationId.AsSpan(migrationId.IndexOf('_') + 1).StartsWith("Resourcefull_", StringComparison.Ordinal);

    internal DbSet<ResourceModel> Resources { get; set; }
    internal DbSet<ResourceChunkModel> ResourceChunks { get; set; }
    internal DbSet<ResourceContainerModel> ResourceContainers { get; set; }

    /// <summary>Always <see langword="null"/>: SQLite has no schemas, and the tables' names stay unqualified.</summary>
    internal string? Schema => null;

    /// <summary>The model's tables.</summary>
    internal static readonly string[] TableNames = ["Resources", "ResourceChunks", "ResourceContainers"];

    /// <summary>The SQL unchanged, as SQLite has no schemas to qualify its tables with.</summary>
    internal static string QualifyTables(string sql, string? schema) => sql;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no type for times, and EF Core can't order or compare the text it would otherwise keep, so every
        // time is stored as its UTC ticks, which order and compare as the instants do.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
}
