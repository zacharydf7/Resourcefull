using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;

namespace Resourcefull.SqlServer;

/// <summary>
/// Puts the migrations' tables in the schema given to <c>UseSqlServer</c>. The migrations and their operations name no
/// schema, so EF Core can still find their tables in each migration's model, for example to rebuild the indexes on a
/// column it alters. Only the SQL written from them names the schema: <see cref="SchemaSqlGenerationHelper"/> qualifies
/// the statements EF Core generates, and <see cref="Generate"/> qualifies the migrations' raw SQL, as
/// <see cref="ResourceSqlRepository"/> qualifies its own.
/// </summary>
internal sealed class SchemaMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies,
    ICommandBatchPreparer commandBatchPreparer) : SqlServerMigrationsSqlGenerator(InSchema(dependencies), commandBatchPreparer)
{
    public override IReadOnlyList<MigrationCommand> Generate(IReadOnlyList<MigrationOperation> operations, IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        if (SchemaOf(Dependencies) is { } schema)
        {
            foreach (SqlOperation sql in operations.OfType<SqlOperation>())
                sql.Sql = ResourceDbContext.QualifyTables(sql.Sql, schema);
        }
        return base.Generate(operations, model, options);
    }

    private static MigrationsSqlGeneratorDependencies InSchema(MigrationsSqlGeneratorDependencies dependencies) =>
        SchemaOf(dependencies) is { } schema
            ? dependencies with { SqlGenerationHelper = new SchemaSqlGenerationHelper(dependencies.SqlGenerationHelper, schema) }
            : dependencies;

    private static string? SchemaOf(MigrationsSqlGeneratorDependencies dependencies) =>
        (dependencies.CurrentContext.Context as ResourceDbContext)?.Schema;
}
