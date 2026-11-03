using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull;

/// <summary>
/// Provisions and upgrades the database a store's provider keeps, such as SQL Server's.
/// </summary>
public static class ResourcefullDatabaseExtensions
{
    /// <summary>
    /// Brings the database of the store added by <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> up
    /// to date. It applies only what the database is missing, so running it again does nothing. A provider with no
    /// database, such as the in-memory one, has nothing to do.
    /// </summary>
    /// <remarks>
    /// With SQL Server, it creates the database if it doesn't exist, and the schema given to <c>UseSqlServer</c> if
    /// there is one. If it's canceled while a migration is applying, that migration rolls back. Run it from a
    /// deployment step, or at the startup of a single instance: a migration can change settings of the whole database.
    /// On SQL Server, for example, turning on <c>READ_COMMITTED_SNAPSHOT</c>, which one migration does on a database
    /// that doesn't have it yet, disconnects every other session on the database.
    /// </remarks>
    /// <param name="services">The application's services, with Resourcefull added.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <exception cref="InvalidOperationException">Resourcefull isn't added.</exception>
    public static Task MigrateResourcefullDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
        => MigrateAsync(services, null, cancellationToken);

    /// <summary>
    /// Brings the database of the store named <paramref name="storeName"/> up to date, as
    /// <see cref="MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/> does for the store without a
    /// name. Migrate every store that keeps a database, including stores that share one with a schema of their own.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's services, with the store added.</param>
    /// <param name="storeName">The name the store was added with, by <c>AddKeyedResourcefull</c>.</param>
    /// <param name="cancellationToken">Cancels the migration.</param>
    /// <exception cref="ArgumentException"><paramref name="storeName"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">No store has the name.</exception>
    public static Task MigrateResourcefullDatabaseAsync(this IServiceProvider services, string storeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        return MigrateAsync(services, storeName, cancellationToken);
    }

    private static Task MigrateAsync(IServiceProvider services, string? storeName, CancellationToken cancellationToken) =>
        DatabaseOf(services, storeName, nameof(MigrateResourcefullDatabaseAsync)) is { } database
            ? database.MigrateAsync(cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// Returns the script that brings the database of the store added by
    /// <see cref="ResourcefullServiceCollectionExtensions.AddResourcefull"/> up to date, so a database administrator can run what
    /// <see cref="MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/> would. It applies only what the
    /// database is missing, so running it again does nothing. Generating it doesn't connect to the database.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's services, with Resourcefull added.</param>
    /// <returns>The script, in the form the provider's database tools run, such as batches separated by <c>GO</c> for SQL Server.</returns>
    /// <exception cref="InvalidOperationException">Resourcefull isn't added.</exception>
    /// <exception cref="NotSupportedException">The store's provider keeps no database.</exception>
    public static string GenerateResourcefullScript(this IServiceProvider services) => GenerateScript(services, null);

    /// <summary>
    /// Returns the script for the database of the store named <paramref name="storeName"/>, as
    /// <see cref="GenerateResourcefullScript(IServiceProvider)"/> does for the store without a name.
    /// <para>Coming in 4.0.</para>
    /// </summary>
    /// <param name="services">The application's services, with the store added.</param>
    /// <param name="storeName">The name the store was added with, by <c>AddKeyedResourcefull</c>.</param>
    /// <returns>The script, in the form the provider's database tools run.</returns>
    /// <exception cref="ArgumentException"><paramref name="storeName"/> is null, empty or white space.</exception>
    /// <exception cref="InvalidOperationException">No store has the name.</exception>
    /// <exception cref="NotSupportedException">The store's provider keeps no database.</exception>
    public static string GenerateResourcefullScript(this IServiceProvider services, string storeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        return GenerateScript(services, storeName);
    }

    private static string GenerateScript(IServiceProvider services, string? storeName) =>
        (DatabaseOf(services, storeName, nameof(GenerateResourcefullScript))
            ?? throw new NotSupportedException(storeName is null
                ? $"Resourcefull's provider keeps no database, so {nameof(GenerateResourcefullScript)} has no script to write."
                : $"The store named '{storeName}' keeps no database, so {nameof(GenerateResourcefullScript)} has no script to write."))
            .GenerateScript();

    /// <summary>
    /// The store's database; <see langword="null"/> when its provider keeps none, and an exception naming
    /// <paramref name="caller"/> when there's no such store.
    /// </summary>
    private static ResourceDatabase? DatabaseOf(IServiceProvider services, string? storeName, string caller)
    {
        if (services.GetStoreService<ResourceDatabase>(storeName) is { } database) return database;
        // A container that can't say what's registered can't tell a missing store from one without a database.
        bool added = storeName is null
            ? services.GetService<IServiceProviderIsService>()?.IsService(typeof(IResourceServiceClient)) ?? true
            : services.GetService<IServiceProviderIsKeyedService>()?.IsKeyedService(typeof(IResourceServiceClient), storeName) ?? true;
        if (!added)
        {
            throw new InvalidOperationException(storeName is null
                ? $"{caller} needs Resourcefull added with {nameof(ResourcefullServiceCollectionExtensions.AddResourcefull)}."
                : $"{caller} needs a store named '{storeName}' added with {nameof(ResourcefullServiceCollectionExtensions.AddKeyedResourcefull)}.");
        }
        return null;
    }
}
