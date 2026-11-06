namespace Resourcefull.Providers;

/// <summary>
/// The database a store's provider keeps, which
/// <see cref="ResourcefullDatabaseExtensions.MigrateResourcefullDatabaseAsync(IServiceProvider, CancellationToken)"/> and
/// <see cref="ResourcefullDatabaseExtensions.GenerateResourcefullScript(IServiceProvider)"/> provision. A provider that
/// keeps one registers it as a singleton under the store's name, <see cref="ResourcefullOptionsBuilder.StoreName"/>. A
/// provider with nothing to provision, such as the in-memory one, registers none.
/// </summary>
public abstract class ResourceDatabase
{
    /// <summary>
    /// Brings the database up to date, applying only what it's missing, so running it again does nothing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the migration.</param>
    public abstract Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a script that does what <see cref="MigrateAsync"/> would, for a database administrator to run. Generating
    /// it doesn't connect to the database.
    /// </summary>
    public abstract string GenerateScript();
}
