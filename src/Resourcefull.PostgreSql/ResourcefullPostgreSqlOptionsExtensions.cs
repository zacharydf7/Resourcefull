using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.PostgreSql;
using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>
/// Configures Resourcefull to store resources in PostgreSQL.
/// <para>Coming in 4.0.</para>
/// </summary>
public static partial class ResourcefullPostgreSqlOptionsExtensions
{
    /// <summary>The SQLSTATE codes retried besides Npgsql's transient ones: a deadlock, and a serialization failure.</summary>
    private static readonly string[] RetriedErrors = ["40P01", "40001"];

    /// <summary>
    /// Stores resources in the PostgreSQL database that <paramref name="connectionString"/> names, retrying transient
    /// failures (including deadlocks). Provision the database with
    /// <see cref="ResourcefullDatabaseExtensions.MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/>
    /// or the script <see cref="ResourcefullDatabaseExtensions.GenerateResourcefullScript(IServiceProvider)"/> writes.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <param name="configure">Sets the schema and EF Core's PostgreSQL options; <see langword="null"/> keeps the defaults.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentException">
    /// The schema isn't a regular identifier of up to 63 characters: a letter or underscore, then letters, digits or
    /// underscores.
    /// </exception>
    public static ResourcefullOptionsBuilder UsePostgreSql(this ResourcefullOptionsBuilder builder, string connectionString,
        Action<ResourcefullPostgreSqlOptions>? configure = null)
    {
        var settings = new ResourcefullPostgreSqlOptions();
        configure?.Invoke(settings);
        string? schema = settings.Schema;
        if (schema is not null && !SchemaName().IsMatch(schema))
        {
            throw new ArgumentException(
                "A schema name must start with a letter or underscore, followed by up to 62 letters, digits or underscores.",
                nameof(configure));
        }
        // Each store has its own contexts, so its factory and everything that uses it are registered under its name.
        string? storeName = builder.StoreName;
        builder.UseProvider(services => new ResourcePostgreSqlRepository(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), services.GetService<TimeProvider>(),
            storeName, services.SearchableMetadataKeys(storeName)));

        builder.Services.AddKeyedSingleton<IDbContextFactory<ResourceDbContext>>(storeName, (services, _) =>
        {
            DbContextOptionsBuilder<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
                .UseApplicationServiceProvider(services);
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.ExecutionStrategy(dependencies => new RetryCountingExecutionStrategy(dependencies,
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorCodesToAdd: RetriedErrors, storeName));
                if (schema is not null) npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema);
                settings.ConfigurePostgreSql?.Invoke(npgsql);
            });
            options.ReplaceService<IModelCacheKeyFactory, SchemaModelCacheKeyFactory>()
                .ReplaceService<IMigrationsSqlGenerator, SchemaMigrationsSqlGenerator>();
            if (schema is not null)
            {
                ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(new SchemaOptionsExtension(schema));
                // The migrations' snapshot has no schema, so the model in one always looks changed.
                options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
            }
            // Last, apply the application's ConfigureDbContext calls to every store's contexts, as
            // AddDbContextFactory would.
            foreach (IDbContextOptionsConfiguration<ResourceDbContext> configuration in
                services.GetServices<IDbContextOptionsConfiguration<ResourceDbContext>>())
                configuration.Configure(services, options);
            return new ResourceDbContextFactory(options.Options);
        });
        builder.Services.AddKeyedSingleton<ResourceDatabase>(storeName, (services, _) => new PostgreSqlDatabase(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), schema, storeName));
        return builder;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}\z")]
    private static partial Regex SchemaName();
}
