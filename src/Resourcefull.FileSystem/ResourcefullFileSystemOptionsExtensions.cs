using Microsoft.Extensions.DependencyInjection;
using Resourcefull.ObjectStorage;

namespace Resourcefull.FileSystem;

/// <summary>
/// Configures Resourcefull to store resources in a directory.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullFileSystemOptionsExtensions
{
    /// <summary>
    /// Stores resources in <paramref name="rootPath"/>, a directory that must exist, on a local disk or a network share.
    /// Resourcefull keeps everything under it, so give it a directory of its own. Every process that stores in it must
    /// reach the same files, and the filesystem must honor the locks one process takes on a file against the others, as
    /// local disks and Windows file shares do.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="rootPath">The directory that holds Resourcefull's files; a relative path is taken from the current directory.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A provider has already been configured.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootPath"/> is null, empty or whitespace (<see cref="ArgumentNullException"/> when null), or not a
    /// valid path.
    /// </exception>
    public static ResourcefullOptionsBuilder UseFileSystem(this ResourcefullOptionsBuilder builder, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        string root = Path.GetFullPath(rootPath);

        string? storeName = builder.StoreName;
        // One store for every scope, so this process's writers queue on its in-process gates before they contend with other
        // processes for the lock files.
        builder.UseProvider(services => new ObjectStoreRepository(new FileSystemObjectStore(root), "", services.GetService<TimeProvider>(),
            storeName, services.SearchableMetadataKeys(storeName)), ServiceLifetime.Singleton);
        return builder;
    }
}
