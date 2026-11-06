using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;
using Resourcefull.SqlServer;

namespace Resourcefull;

/// <summary>
/// Configures Resourcefull to store resources in SQL Server.
/// </summary>
public static partial class ResourcefullSqlServerOptionsExtensions
{
    /// <summary>
    /// Stores resources in the SQL Server database that <paramref name="connectionString"/> names, retrying transient
    /// failures (including deadlocks). Provision the database with
    /// <see cref="ResourcefullDatabaseExtensions.MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/>
    /// or the packaged scripts.
    /// Also registers <see cref="ContentHashBackfill"/>, under the store's name for a store added with
    /// <c>AddKeyedResourcefull</c>.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <param name="configure">Sets the schema and EF Core's SQL Server options; <see langword="null"/> keeps the defaults.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentException">
    /// The schema isn't a regular identifier of up to 128 characters: a letter or underscore, then letters, digits or
    /// underscores.
    /// </exception>
    public static ResourcefullOptionsBuilder UseSqlServer(this ResourcefullOptionsBuilder builder, string connectionString,
        Action<ResourcefullSqlServerOptions>? configure = null)
    {
        var settings = new ResourcefullSqlServerOptions();
        configure?.Invoke(settings);
        string? schema = settings.Schema;
        if (schema is not null && !SchemaName().IsMatch(schema))
        {
            throw new ArgumentException(
                "A schema name must start with a letter or underscore, followed by up to 127 letters, digits or underscores.", nameof(configure));
        }
        // Each store has its own contexts, so its factory and everything that uses it are registered under its name.
        string? storeName = builder.StoreName;
        builder.UseProvider(services => new ResourceSqlRepository(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), services.GetService<TimeProvider>(),
            storeName, services.SearchableMetadataKeys(storeName)));

        builder.Services.AddKeyedSingleton<IDbContextFactory<ResourceDbContext>>(storeName, (services, _) =>
        {
            DbContextOptionsBuilder<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
                .UseApplicationServiceProvider(services);
            options.UseSqlServer(connectionString, sql =>
            {
                sql.ExecutionStrategy(dependencies => new RetryCountingExecutionStrategy(dependencies,
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: [1205, ResourceSqlRepository.NameLockNotGrantedError], storeName));
                if (schema is not null) sql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema);
                settings.ConfigureSqlServer?.Invoke(sql);
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
        builder.Services.AddKeyedSingleton<ResourceDatabase>(storeName, (services, _) => new SqlServerDatabase(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), storeName));
        builder.Services.AddKeyedScoped(storeName, (services, _) => new ContentHashBackfill(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), storeName));
        return builder;
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}\z")]
    private static partial Regex SchemaName();
}