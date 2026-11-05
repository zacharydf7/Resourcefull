using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull.Sqlite;

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
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    public static ResourcefullOptionsBuilder UseSqlite(this ResourcefullOptionsBuilder builder, string connectionString)
        => UseSqlite(builder, connectionString, _ => { });

    /// <summary>
    /// Stores resources in SQLite as <see cref="UseSqlite(ResourcefullOptionsBuilder, string)"/> does, then applies
    /// <paramref name="configure"/> to EF Core's SQLite options, to change the command timeout, for example: how long a
    /// write waits for another to finish before it fails, 30 seconds by default.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="connectionString">The connection string of the database that holds Resourcefull's tables.</param>
    /// <param name="configure">Changes EF Core's SQLite options, after Resourcefull has set its own.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public static ResourcefullOptionsBuilder UseSqlite(this ResourcefullOptionsBuilder builder, string connectionString,
        Action<SqliteDbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        // Each store has its own contexts, so its factory and everything that uses it are registered under its name.
        string? storeName = builder.StoreName;
        builder.UseProvider(services => new ResourceSqliteRepository(
            services.GetRequiredStoreService<IDbContextFactory<ResourceDbContext>>(storeName), services.GetService<TimeProvider>(),
            storeName, services.SearchableMetadataKeys(storeName)));

        builder.Services.AddKeyedSingleton<IDbContextFactory<ResourceDbContext>>(storeName, (services, _) =>
        {
            DbContextOptionsBuilder<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
                .UseApplicationServiceProvider(services);
            options.UseSqlite(connectionString, configure);
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
