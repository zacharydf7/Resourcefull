using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace Resourcefull.PostgreSql;

#pragma warning disable EF1001 // Npgsql's generator takes its singleton options, an internal type, so a subclass names it too.
/// <summary>
/// Puts the migrations' tables in the schema given to <c>UsePostgreSql</c>. The migrations and their operations name no
/// schema, so EF Core can still find their tables in each migration's model; only the SQL written from them names the
/// schema: <see cref="SchemaSqlGenerationHelper"/> adds it to table names, as <see cref="ResourcePostgreSqlRepository"/>
/// qualifies its own, and this class adds it to the name of an index being dropped.
/// </summary>
internal sealed class SchemaMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions) : NpgsqlMigrationsSqlGenerator(InSchema(dependencies), npgsqlSingletonOptions)
#pragma warning restore EF1001
{
    private static MigrationsSqlGeneratorDependencies InSchema(MigrationsSqlGeneratorDependencies dependencies) =>
        SchemaOf(dependencies) is { } schema
            ? dependencies with { SqlGenerationHelper = new SchemaSqlGenerationHelper(dependencies.SqlGenerationHelper, schema) }
            : dependencies;

    /// <summary>
    /// Qualifies the name of an index a migration drops: <c>DROP INDEX</c> names no table, so PostgreSQL would look for the
    /// bare name on the search path, and <see cref="SchemaSqlGenerationHelper"/> qualifies only tables.
    /// </summary>
    protected override void Generate(DropIndexOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        operation.Schema = SchemaOf(Dependencies);
        base.Generate(operation, model, builder, terminate);
    }

    private static string? SchemaOf(MigrationsSqlGeneratorDependencies dependencies) =>
        (dependencies.CurrentContext.Context as ResourceDbContext)?.Schema;
}
