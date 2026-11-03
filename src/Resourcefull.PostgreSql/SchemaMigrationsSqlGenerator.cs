using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace Resourcefull.PostgreSql;

#pragma warning disable EF1001 // Npgsql's generator takes its singleton options, an internal type, so a subclass names it too.
/// <summary>
/// Puts the migrations' tables in the schema given to <c>UsePostgreSql</c>. The migrations and their operations name no
/// schema, so EF Core can still find their tables in each migration's model; only the SQL written from them names the
/// schema, through <see cref="SchemaSqlGenerationHelper"/>, as <see cref="ResourcePostgreSqlRepository"/> qualifies its
/// own.
/// </summary>
internal sealed class SchemaMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions) : NpgsqlMigrationsSqlGenerator(InSchema(dependencies), npgsqlSingletonOptions)
#pragma warning restore EF1001
{
    private static MigrationsSqlGeneratorDependencies InSchema(MigrationsSqlGeneratorDependencies dependencies) =>
        (dependencies.CurrentContext.Context as ResourceDbContext)?.Schema is { } schema
            ? dependencies with { SqlGenerationHelper = new SchemaSqlGenerationHelper(dependencies.SqlGenerationHelper, schema) }
            : dependencies;
}
