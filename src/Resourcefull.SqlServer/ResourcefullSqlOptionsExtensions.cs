using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.SqlServer;

/// <summary>
/// Configures Resourcefull to store resources in SQL Server.
/// </summary>
public static partial class ResourcefullSqlOptionsExtensions
{
    /// <summary>
    /// Stores resources in the SQL Server database that <paramref name="connectionString"/> names, retrying transient
    /// failures (including deadlocks). Provision the database with
    /// <see cref="ResourcefullSqlDatabaseExtensions.MigrateResourcefullDatabaseAsync"/> or the packaged scripts.
    /// Also registers <see cref="ContentHashBackfill"/>.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public static ResourcefullOptionsBuilder UseSqlServer(this ResourcefullOptionsBuilder builder, string connectionString)
        => UseSqlServer(builder, connectionString, _ => { });

    /// <summary>
    /// Stores resources in SQL Server as <see cref="UseSqlServer(ResourcefullOptionsBuilder, string)"/> does, then
    /// applies <paramref name="configure"/> to EF Core's SQL Server options, to change the command timeout or the
    /// retries, for example. Replacing the retry policy, such as with <c>EnableRetryOnFailure</c>, also stops
    /// Resourcefull counting retries in <c>resourcefull.sqlserver.retries</c>; give it error 50001 to retry, which a
    /// write whose lock on its resource name lost a deadlock raises.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <param name="configure">Changes EF Core's SQL Server options, after Resourcefull has set its own.</param>
    /// <param name="schema">
    /// The schema that holds Resourcefull's tables, such as <c>resourcefull</c>, so they don't mix with an application's
    /// own tables in <c>dbo</c>. Resourcefull's migration history is then kept in <c>__EFMigrationsHistory</c> in that
    /// schema too. <see langword="null"/>, the default, leaves the tables' names unqualified, as in 3.x, so they're in
    /// the database user's default schema, normally <c>dbo</c>.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schema"/> isn't a regular identifier of up to 128 characters: a letter or underscore, then
    /// letters, digits or underscores.
    /// </exception>
    public static ResourcefullOptionsBuilder UseSqlServer(this ResourcefullOptionsBuilder builder, string connectionString,
        Action<SqlServerDbContextOptionsBuilder> configure, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (schema is not null && !SchemaName().IsMatch(schema))
        {
            throw new ArgumentException(
                "A schema name must start with a letter or underscore, followed by up to 127 letters, digits or underscores.", nameof(schema));
        }
        builder.UseProvider<ResourceSqlRepository>();

        builder.Services.AddDbContextFactory<ResourceDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.ExecutionStrategy(dependencies => new RetryCountingExecutionStrategy(dependencies,
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: [1205, ResourceSqlRepository.NameLockNotGrantedError]));
                if (schema is not null) sql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema);
                configure(sql);
            });
            options.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>()
                .ReplaceService<IMigrationsSqlGenerator, SchemaMigrationsSqlGenerator>();
            if (schema is not null)
            {
                ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new SchemaOptionsExtension(schema));
                // The migrations' snapshot has no schema, so the model in one always looks changed.
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            }
        });
        builder.Services.AddScoped(services => new ContentHashBackfill(services.GetRequiredService<IDbContextFactory<ResourceDbContext>>()));
        return builder;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex SchemaName();
}