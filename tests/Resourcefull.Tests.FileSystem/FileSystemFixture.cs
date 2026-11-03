using Microsoft.Extensions.DependencyInjection;
using Resourcefull.FileSystem;

namespace Resourcefull.Tests.FileSystem;

/// <summary>A throwaway directory for the suite, under the system's temporary directory, deleted on teardown.</summary>
public sealed class FileSystemFixture : IDisposable
{
    /// <summary>The run's directory.</summary>
    public string Root { get; } = Directory.CreateTempSubdirectory("resourcefull-it-").FullName;

    /// <summary>A directory of its own under <see cref="Root"/>, for a test that looks at the files.</summary>
    public string NewRoot() => Directory.CreateDirectory(Path.Combine(Root, Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Builds a fully wired Resourcefull service provider on <paramref name="root"/>, or on the run's directory.</summary>
    public ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null, string? root = null)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseFileSystem(root ?? Root);
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

[CollectionDefinition(Name)]
public sealed class FileSystemCollection : ICollectionFixture<FileSystemFixture>
{
    public const string Name = "FileSystem";
}
