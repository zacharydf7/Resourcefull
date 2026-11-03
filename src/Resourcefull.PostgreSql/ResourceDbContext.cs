using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Resourcefull.PostgreSql.Models;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Resourcefull.PostgreSql;

internal class ResourceDbContext(DbContextOptions<ResourceDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Byte-order collation for names and version IDs: case-sensitive, the same on every server regardless of its
    /// locale. It orders by Unicode code point, which differs from .NET's ordinal order only between characters beyond
    /// U+FFFF and those from U+E000 to U+FFFF.
    /// </summary>
    internal const string NameCollation = "C";

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

    /// <summary>
    /// The schema given to <c>UsePostgreSql</c>, or <see langword="null"/> without one: the tables' names are then left
    /// unqualified, so they're found through the connection's search path, normally in <c>public</c>.
    /// </summary>
    internal string? Schema { get; } = options.FindExtension<SchemaOptionsExtension>()?.Schema;

    /// <summary>The model's tables, which go in the schema given to <c>UsePostgreSql</c> when there is one.</summary>
    internal static readonly string[] TableNames = ["Resources", "ResourceChunks", "ResourceContainers"];

    private static readonly Regex TableName = new($@"(?<!\.)""({string.Join("|", TableNames)})""", RegexOptions.Compiled);

    /// <summary>
    /// Qualifies the tables named in Resourcefull's own SQL with <paramref name="schema"/>, or returns the SQL unchanged
    /// when it's <see langword="null"/>. That SQL always writes them in double quotes, such as <c>"Resources"</c>.
    /// </summary>
    internal static string QualifyTables(string sql, string? schema) =>
        schema is null ? sql : TableName.Replace(sql, $"\"{schema}\".$0");

    /// <summary>
    /// Qualifies the tables in an interpolated query, keeping its values as parameters. There's deliberately no
    /// instance overload that takes only a string: an interpolated string would bind to it and lose its parameters.
    /// </summary>
    internal FormattableString QualifyTables(FormattableString sql) =>
        Schema is null ? sql : FormattableStringFactory.Create(QualifyTables(sql.Format, Schema), sql.GetArguments());

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // PostgreSQL keeps an instant, not an offset, and Npgsql writes only offset zero: every time is stored in UTC.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Schema is not null) modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }

    private sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
        value => value.ToUniversalTime(), value => value);
}
