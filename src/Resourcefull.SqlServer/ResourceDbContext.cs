using Microsoft.EntityFrameworkCore;
using Resourcefull.SqlServer.Models;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

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
    internal DbSet<ResourceMetadataModel> ResourceMetadata { get; set; }

    /// <summary>
    /// The schema given to <c>UseSqlServer</c>, or <see langword="null"/> without one: the tables' names are then left
    /// unqualified, as before schemas could be chosen, so they're in the database user's default schema.
    /// </summary>
    internal string? Schema { get; } = options.FindExtension<SchemaOptionsExtension>()?.Schema;

    /// <summary>The model's tables, which go in the schema given to <c>UseSqlServer</c> when there is one.</summary>
    internal static readonly string[] TableNames = ["Resources", "ResourceChunks", "ResourceContainers", "ResourceMetadata"];

    private static readonly Regex TableName = new($@"(?<!\.)\[({string.Join("|", TableNames)})\]", RegexOptions.Compiled);

    /// <summary>
    /// Qualifies the tables named in Resourcefull's own SQL with <paramref name="schema"/>, or returns the SQL unchanged
    /// when it's <see langword="null"/>. That SQL always writes them in brackets, such as <c>[Resources]</c>.
    /// </summary>
    internal static string QualifyTables(string sql, string? schema) =>
        schema is null ? sql : TableName.Replace(sql, $"[{schema}].$0");

    /// <summary>
    /// Qualifies the tables in an interpolated query, keeping its values as parameters. There's deliberately no
    /// instance overload that takes only a string: an interpolated string would bind to it and lose its parameters.
    /// </summary>
    internal FormattableString QualifyTables(FormattableString sql) =>
        Schema is null ? sql : FormattableStringFactory.Create(QualifyTables(sql.Format, Schema), sql.GetArguments());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Schema is not null) modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}