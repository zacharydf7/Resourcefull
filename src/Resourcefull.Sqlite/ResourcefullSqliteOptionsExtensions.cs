using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;
using Resourcefull.Sqlite;

namespace Resourcefull;

/// <summary>
/// Configures Resourcefull to store resources in SQLite.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullSqliteOptionsExtensions
{
    /// <summary>
    /// Stores resources in the SQLite database that <paramref name="connectionString"/> names, such as
    /// <c>Data Source=files.db</c>. Provision it with
    /// <see cref="ResourcefullDatabaseExtensions.MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/>,
    /// which also turns on write-ahead logging, so reads don't wait for writes.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <param name="configure">Sets EF Core's SQLite options; <see langword="null"/> keeps the defaults.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public static ResourcefullOptionsBuilder UseSqlite(this ResourcefullOptionsBuilder builder, string connectionString,
        Action<ResourcefullSqliteOptions>? configure = null)
    {
        var settings = new ResourcefullSqliteOptions();
        configure?.Invoke(settings);
        // Each store has its own contexts, so its factory and everything that uses it are registered under its name.
        string? storeName = builder.StoreName;
        builder.UseProvider(services => new ResourceSqliteRepository(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), services.GetService<TimeProvider>(),
            storeName, services.SearchableMetadataKeys(storeName)));

        builder.Services.AddKeyedSingleton<IDbContextFactory<ResourceDbContext>>(storeName, (services, _) =>
        {
            DbContextOptionsBuilder<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
                .UseApplicationServiceProvider(services);
            options.UseSqlite(connectionString, settings.ConfigureSqlite);
            // Last, apply the application's ConfigureDbContext calls to every store's contexts, as
            // AddDbContextFactory would.
            foreach (IDbContextOptionsConfiguration<ResourceDbContext> configuration in
                services.GetServices<IDbContextOptionsConfiguration<ResourceDbContext>>())
                configuration.Configure(services, options);
            return new ResourceDbContextFactory(options.Options);
        });
        builder.Services.AddKeyedSingleton<ResourceDatabase>(storeName, (services, _) => new SqliteDatabase(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), storeName));
        return builder;
    }
}
