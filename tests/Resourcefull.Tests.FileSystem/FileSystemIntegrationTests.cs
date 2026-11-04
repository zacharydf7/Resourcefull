using Microsoft.Extensions.DependencyInjection;
using Resourcefull.FileSystem;
using Resourcefull.ObjectStorage;
using Resourcefull.Providers;

namespace Resourcefull.Tests.FileSystem;

[Collection(FileSystemCollection.Name)]
public class FileSystemIntegrationTests(FileSystemFixture fixture)
{
    public static TheoryData<string> Names => new()
    {
        "report.pdf", "Report.PDF", "..", ".", "a/../b", @"a\b", "[A]", "a b", "a:b<c>d?e|f*g\"h", "~^", "CON", "nul.txt",
        "名前", "😀/😀", "é", "é", new string('a', 256), new string('.', 256), new string('é', 256),
        new string('名', 256), string.Concat(Enumerable.Repeat("😀", 128))
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Escape_ShouldGiveFileNamesThatEveryFilesystemTakesAndThatUnescapeToTheKey_WhenGivenAName(string name)
    {
        // Arrange: the name's head key, with segments as short as the store asks for.
        string key = new ObjectKeys("", new FileSystemObjectStore(fixture.Root).KeySegmentBytes).Head(new string('0', 32), name);

        // Act
        string[] fileNames = [.. key.Split('/').Select(FileSystemObjectStore.Escape)];

        // Assert: each fits in 255 characters, has no character a filesystem refuses or changes, no uppercase letter and
        // no leading dot, and stands for its segment.
        Assert.All(fileNames, fileName =>
        {
            Assert.InRange(fileName.Length, 1, 255);
            Assert.All(fileName, c => Assert.True(c is > ' ' and < '\x7F' && !"\"*/:<>?\\|.".Contains(c) && !char.IsUpper(c), fileName));
        });
        Assert.Equal(key.Split('/'), fileNames.Select(FileSystemObjectStore.Unescape));
    }

    [Theory]
    [InlineData("Report.pdf", "report.pdf")]
    [InlineData("é.txt", "é.txt")]
    public async Task UploadAsync_ShouldKeepBothNames_WhenTheyDifferOnlyInCaseOrUnicodeNormalization(string first, string second)
    {
        // Arrange
        await using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateContainerAsync($"c-{Guid.NewGuid():N}");

        // Act
        await container.UploadAsync(first, BinaryData.FromString("first"));
        await container.UploadAsync(second, BinaryData.FromString("second"));

        // Assert
        Assert.Equal("first", (await container.DownloadContentAsync(first))!.Content.ToString());
        Assert.Equal("second", (await container.DownloadContentAsync(second))!.Content.ToString());
        Assert.Equal(2, (await container.ListResourcesAsync().ToListAsync()).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_ShouldKeepBothNames_WhenOneNameIsAnotherWhoseKeyEndsAtASegment(bool longerFirst)
    {
        // Arrange: a name whose escaped form fills a segment, and the same name with more after it, which goes under the
        // first one's directory.
        await using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateContainerAsync($"c-{Guid.NewGuid():N}");
        string shorter = new('a', new FileSystemObjectStore(fixture.Root).KeySegmentBytes - ObjectKeys.LongestUnit + 1);
        string longer = shorter + "b";
        string[] order = longerFirst ? [longer, shorter] : [shorter, longer];

        // Act
        foreach (string name in order) await container.UploadAsync(name, BinaryData.FromString(name.Length.ToString()));

        // Assert
        Assert.Equal(shorter.Length.ToString(), (await container.DownloadContentAsync(shorter))!.Content.ToString());
        Assert.Equal(longer.Length.ToString(), (await container.DownloadContentAsync(longer))!.Content.ToString());
        Assert.Equal([shorter, longer], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    public static TheoryData<string> LongestNames => new() { new string('\u00e9', 256), string.Concat(Enumerable.Repeat("😀", 128)) };

    [Theory]
    [MemberData(nameof(LongestNames))]
    public async Task UploadAsync_ShouldStoreReadAndListTheName_WhenItIsAsLongAsANameCanBe(string name)
    {
        // macOS allows a path only 1,023 bytes, too few for these; the next test covers it.
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS refuses a path this long.");

        // Arrange
        await using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateContainerAsync($"c-{Guid.NewGuid():N}");

        // Act
        await container.UploadAsync(name, BinaryData.FromString("long"));

        // Assert
        Assert.Equal("long", (await container.DownloadContentAsync(name))!.Content.ToString());
        Assert.Equal([name], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task DeleteAsync_ShouldDeleteTheFile_WhenAnotherHandleReleasesItDuringTheDelete()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete a file another handle has open.");

        // Arrange: a stored object, held open with no sharing, as a virus scanner may hold a file just written.
        string root = fixture.NewRoot();
        var store = new FileSystemObjectStore(root);
        await store.UploadAsync("k", new MemoryStream("a"u8.ToArray()), CancellationToken.None);
        string path = Path.Combine(root, "k");
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        // Act
        Task deleting = store.DeleteAsync(["k"], CancellationToken.None);
        await Task.Delay(200);
        bool waited = !deleting.IsCompleted;
        await held.DisposeAsync();
        await deleting;

        // Assert: it waited for the handle, then deleted the file.
        Assert.True(waited);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowResourceNameExceptionAndFindNothing_WhenTheNamesPathIsLongerThanTheSystemAllows()
    {
        // Arrange: macOS's limit on a path, and a name whose file's path would pass it.
        string root = fixture.NewRoot();
        var repository = new ObjectStoreRepository(new FileSystemObjectStore(root) { MaxPathBytes = 1023 }, "", null, null);
        var location = new ResourceLocation("c", new string('\u00e9', 256));
        await repository.AddContainerAsync("c", new Dictionary<string, string>());

        // Act
        ResourceNameException refused = await Assert.ThrowsAsync<ResourceNameException>(() =>
            repository.AddResourceAsync(new NewResourceVersion(location, "v1"), new MemoryStream("a"u8.ToArray())));

        // Assert: refused with the limit named but not the directory, since clients may see the message, and a lookup
        // or a listing finds nothing rather than failing.
        Assert.Contains("1023", refused.Message);
        Assert.DoesNotContain(root, refused.Message);
        Assert.Null(await repository.GetResourceInfoAsync(location));
        Assert.Empty(await repository.GetAllResourceInfoAsync("c"));
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreOneVersion_WhenProcessesSharingTheDirectoryCreateTheSameNameAtOnce()
    {
        // Arrange: two service providers on one directory, each with a store of its own, as two processes have.
        string root = fixture.NewRoot();
        await using ServiceProvider first = fixture.BuildServiceProvider(root: root);
        await using ServiceProvider second = fixture.BuildServiceProvider(root: root);
        IResourceContainerClient[] containers =
        [
            await first.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c"),
            second.GetRequiredService<IResourceServiceClient>().GetContainerClient("c")
        ];

        // Act: twenty create-only uploads of one name, half through each.
        Task<ResourceInfo>[] uploads =
        [
            .. Enumerable.Range(0, 20).Select(i => Task.Run(() => containers[i % 2].UploadAsync("a.txt", BinaryData.FromString($"{i}"))))
        ];
        Exception?[] failures = await Task.WhenAll(uploads.Select(async upload =>
        {
            try
            {
                await upload;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }));

        // Assert: one stored, every other refused because the name exists.
        Assert.Single(failures, failure => failure is null);
        Assert.All(failures.Where(failure => failure is not null), failure => Assert.IsType<ResourceConflictException>(failure));
        Assert.Single(await containers[1].GetResourceVersionsAsync("a.txt"));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStoreTheVersion_WhenTheMoveReportsTheFileNotFoundAsAnotherWriterRemakesItsDirectory()
    {
        // Arrange: a store whose first move of a version's content into its directory fails as it does when another
        // writer deletes the directory and makes it again just as the move runs: .NET then reports the file as not found.
        bool failed = false;
        var store = new FileSystemObjectStore(fixture.NewRoot())
        {
            BeforeMove = path =>
            {
                string directory = Path.GetDirectoryName(path)!;
                if (failed || Path.GetFileName(directory) != "b" || !Directory.Exists(directory)) return;
                failed = true;
                throw new FileNotFoundException(null, path);
            }
        };
        var repository = new ObjectStoreRepository(store, "", null, null);
        var location = new ResourceLocation("c", "a.txt");
        await repository.AddContainerAsync("c", new Dictionary<string, string>());

        // Act
        await repository.AddResourceAsync(new NewResourceVersion(location, "v1"), new MemoryStream("a"u8.ToArray()));

        // Assert: the move was tried again, and the content is there.
        Assert.True(failed);
        Assert.Equal("a"u8.ToArray(), await ReadAllAsync(await repository.GetResourceStreamAsync(location)));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowAtOnce_WhenTheFileBeingMovedIsGone()
    {
        // Arrange: a store whose file being written goes missing before it's moved into its directory.
        string root = fixture.NewRoot();
        var store = new FileSystemObjectStore(root)
        {
            BeforeMove = path =>
            {
                if (Path.GetFileName(Path.GetDirectoryName(path)) != "b") return;
                foreach (string written in Directory.EnumerateFiles(Path.Combine(root, ".tmp"))) File.Delete(written);
            }
        };
        var repository = new ObjectStoreRepository(store, "", null, null);
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        var started = System.Diagnostics.Stopwatch.StartNew();

        // Act
        await Assert.ThrowsAnyAsync<FileNotFoundException>(() => repository.AddResourceAsync(
            new NewResourceVersion(new ResourceLocation("c", "a.txt"), "v1"), new MemoryStream("a"u8.ToArray())));

        // Assert: refused without waiting out the half minute the move keeps trying for a directory.
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Repository_ShouldThrowDirectoryNotFoundAndCreateNothing_WhenTheDirectoryIsMissing()
    {
        // Arrange
        string root = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N"));
        await using ServiceProvider provider = fixture.BuildServiceProvider(root: root);
        var service = provider.GetRequiredService<IResourceServiceClient>();

        // Act
        DirectoryNotFoundException listing = await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.ListContainersAsync().ToListAsync().AsTask());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.CreateContainerAsync("c"));

        // Assert
        Assert.Contains(root, listing.Message);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ListResourcesAsync_ShouldSkipFilesResourcefullDidNotWrite_WhenADirectoryHoldsThem()
    {
        // Arrange: files an operating system or a person might leave in each directory.
        string root = fixture.NewRoot();
        await using ServiceProvider provider = fixture.BuildServiceProvider(root: root);
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a.txt", BinaryData.FromString("a"));
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Append(root))
        {
            foreach (string stray in (string[])["desktop.ini", ".DS_Store", "Thumbs.db", "notes"])
                await File.WriteAllTextAsync(Path.Combine(directory, stray), "stray");
        }

        // Act
        List<ResourceInfo> resources = await container.ListResourcesAsync().ToListAsync();
        List<string> containers = await provider.GetRequiredService<IResourceServiceClient>().ListContainersAsync()
            .Select(c => c.Name).ToListAsync();

        // Assert: "notes" is a file name a key escapes to, but not one any listed prefix holds.
        Assert.Equal(["a.txt"], resources.Select(r => r.Name));
        Assert.Equal(["c"], containers);
    }

    [Fact]
    public async Task UploadAsync_ShouldDeleteFilesStoppedWritesLeft_WhenTheyAreOverADayOld()
    {
        // Arrange: a stale file and a fresh one in the store's directory of files being written.
        string root = fixture.NewRoot();
        string temp = Directory.CreateDirectory(Path.Combine(root, ".tmp")).FullName;
        string stale = Path.Combine(temp, "stale"), fresh = Path.Combine(temp, "fresh");
        await File.WriteAllTextAsync(stale, "stale");
        await File.WriteAllTextAsync(fresh, "fresh");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        await using ServiceProvider provider = fixture.BuildServiceProvider(root: root);

        // Act
        await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");

        // Assert
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task DeleteContainerAsync_ShouldLeaveNoDirectoriesBehind_WhenEverythingInThemIsDeleted()
    {
        // Arrange: a container holding a long name's version, whose head goes in nested directories.
        string root = fixture.NewRoot();
        var repository = new ObjectStoreRepository(new FileSystemObjectStore(root), "", null, null);
        var location = new ResourceLocation("c", new string('a', 256));
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(location, "v1"), new MemoryStream("a"u8.ToArray()));

        // Act
        await repository.DeleteResourceAsync(location);
        await repository.DeleteContainerAsync("c");

        // Assert: the container's data and the directories that held it are gone; its record stays, marked deleted.
        Assert.Equal([".locks", ".tmp", "c"], Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).Order());
    }

    private static async Task<byte[]> ReadAllAsync(Stream? stream)
    {
        await using Stream content = stream!;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        return copy.ToArray();
    }
}
