using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [Fact]
    public async Task Writes_ShouldRefuseTheName_WhenANewNameHasAForbiddenCharacter()
    {
        // Arrange: the names are here rather than theory data, which xUnit serializes, replacing an unpaired surrogate.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("source", BinaryData.FromString("x"));

        // Act & Assert: every write that could create the name refuses it, and stores nothing.
        foreach (string name in new[] { "line\nbreak", "invoice\u202Efdp.exe", "lone\uD800" })
        {
            await Assert.ThrowsAsync<ResourceNameException>(() => container.UploadAsync(name, BinaryData.FromString("x")));
            await Assert.ThrowsAsync<ResourceNameException>(() => container.CopyResourceAsync("source", container.Name, name));
            await Assert.ThrowsAsync<ResourceNameException>(() => container.RenameResourceAsync("source", name));
            await Assert.ThrowsAsync<ResourceNameException>(() => ServiceOf(provider).CreateContainerAsync(name));
        }
        Assert.Equal(["source"], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task ExistingName_ShouldStayReadableRenamableAndDeletable_WhenItBreaksTheRulesForNewNames()
    {
        // Arrange: names an earlier release could have stored, put straight into the provider.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        const string spoofed = "invoice\u202Efdp.exe", tabbed = "tab\tname";
        foreach (string name in new[] { spoofed, tabbed })
        {
            await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation(container.Name, name), Guid.NewGuid().ToString()),
                new MemoryStream([1]));
        }

        // Act
        ResourceContent? content = await container.DownloadContentAsync(spoofed);
        ResourceInfo? renamed = await container.RenameResourceAsync(spoofed, "invoice.pdf");
        bool deleted = await container.DeleteResourceIfExistsAsync(tabbed);

        // Assert
        Assert.Equal([1], content!.Content.ToArray());
        Assert.Equal("invoice.pdf", renamed!.Name);
        Assert.True(deleted);
        Assert.Equal(["invoice.pdf"], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldRestoreAnOlderName_WhenItBreaksTheRulesForNewNames()
    {
        // Arrange: an export of a name an earlier release could have stored, and a container name that's fine.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await provider.GetRequiredService<ResourceRepository>().AddResourceAsync(
            new NewResourceVersion(new ResourceLocation(container.Name, "tab\tname"), Guid.NewGuid().ToString()), new MemoryStream([1]));
        using var export = new MemoryStream();
        await ServiceOf(provider).ExportContainerAsync(container.Name, export);
        export.Position = 0;
        string restored = NewContainerName();

        // Act
        await ServiceOf(provider).ImportContainerAsync(restored, export);

        // Assert: a backup restores the name as it was.
        ResourceContent? content = await ServiceOf(provider).GetContainerClient(restored).DownloadContentAsync("tab\tname");
        Assert.Equal([1], content!.Content.ToArray());
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldReturnTheContainer_WhenItsOlderNameBreaksTheRulesForNewNames()
    {
        // Arrange: a container an earlier release could have created, put straight into the provider.
        using ServiceProvider provider = BuildServiceProvider();
        string name = NewContainerName() + "\tolder";
        await provider.GetRequiredService<ResourceRepository>().AddContainerAsync(name, new Dictionary<string, string>());

        // Act
        IResourceContainerClient container = await ServiceOf(provider).CreateContainerIfNotExistsAsync(name);

        // Assert
        Assert.Equal(name, container.Name);
        await Assert.ThrowsAsync<ResourceNameException>(() => ServiceOf(provider).CreateContainerIfNotExistsAsync(NewContainerName() + "\tnew"));
    }

    [Fact]
    public async Task CreateContainerAsync_ShouldCreateBoth_WhenNamesDifferOnlyInCase()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        var suffix = Guid.NewGuid().ToString("N");

        // Act
        await service.CreateContainerAsync($"Docs-{suffix}");
        await service.CreateContainerAsync($"docs-{suffix}");

        // Assert
        Assert.True(await service.ContainerExistsAsync($"Docs-{suffix}"));
        Assert.True(await service.ContainerExistsAsync($"docs-{suffix}"));
        Assert.False(await service.ContainerExistsAsync($"DOCS-{suffix}"));
    }

    [Fact]
    public async Task UploadAsync_ShouldKeepBothResources_WhenNamesDifferOnlyInCase()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        await container.UploadAsync("report.txt", BinaryData.FromString("lower"));
        await container.UploadAsync("Report.txt", BinaryData.FromString("upper"));

        // Assert
        Assert.Equal(2, (await container.ListResourcesAsync().ToListAsync()).Count);
        Assert.Equal("lower", (await container.DownloadContentAsync("report.txt"))!.Content.ToString());
        Assert.Equal("upper", (await container.DownloadContentAsync("Report.txt"))!.Content.ToString());
        Assert.Null(await container.GetResourceInfoAsync("REPORT.TXT"));
    }

    [Fact]
    public async Task UploadAsync_ShouldRoundTripTheResource_WhenNamesAreAtTheMaximumLengths()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        var containerName = Guid.NewGuid().ToString("N").PadRight(ResourceNames.MaxContainerNameLength, 'c');
        var resourceName = new string('r', ResourceNames.MaxResourceNameLength);
        var payload = Encoding.UTF8.GetBytes("longest names");
        IResourceContainerClient container = await service.CreateContainerAsync(containerName);

        // Act
        await container.UploadAsync(resourceName, new MemoryStream(payload));
        ResourceContent? content = await container.DownloadContentAsync(resourceName);

        // Assert
        Assert.Equal(payload, content!.Content.ToArray());
    }

    [Fact]
    public async Task UploadAsync_ShouldKeepEachNameAsGiven_WhenNamesLookLikePaths()
    {
        // Arrange: names are opaque, so a provider that stores them as paths, such as on a file system, mustn't resolve
        // "a/../b" to "b", or let ".." reach outside the container.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string[] names = ["b", "..", ".", "./b", "a/../b", "a//b", "a/b", @"a\b", "/b", "b/", "../b"];

        // Act
        foreach (string name in names) await container.UploadAsync(name, BinaryData.FromString(name));

        // Assert
        Assert.Equal(names.Order(StringComparer.Ordinal),
            (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name).Order(StringComparer.Ordinal));
        foreach (string name in names)
            Assert.Equal(name, (await container.DownloadContentAsync(name))!.Content.ToString());
    }

    [Fact]
    public async Task CreateContainerAsync_ShouldKeepContainersApart_WhenNamesLookLikePaths()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        var suffix = Guid.NewGuid().ToString("N");
        string[] names = [suffix, $"../{suffix}", $"x/../{suffix}", $"{suffix}/.."];

        // Act
        foreach (string name in names)
            await (await service.CreateContainerAsync(name)).UploadAsync("r", BinaryData.FromString(name));

        // Assert
        foreach (string name in names)
        {
            Assert.True(await service.ContainerExistsAsync(name));
            ResourceContent? content = await service.GetContainerClient(name).DownloadContentAsync("r");
            Assert.Equal(name, content!.Content.ToString());
        }
    }

    [Fact]
    public async Task ExistsAsync_ShouldFindNothing_WhenGivenAVersionIdThatLooksLikeAPath()
    {
        // Arrange: a provider that stores versions under their name, as paths, mustn't let "../a/<a's version>" reach
        // another name's version through "b".
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo a = await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.UploadAsync("b", BinaryData.FromString("b"));
        string escape = $"../a/{a.VersionId}";

        // Act
        bool exists = await container.ExistsAsync("b", escape);
        ResourceContent? content = await container.DownloadContentAsync("b", escape);
        bool deleted = await container.DeleteResourceIfExistsAsync("b", escape);

        // Assert
        Assert.False(exists);
        Assert.Null(content);
        Assert.False(deleted);
        Assert.Equal("a", (await container.DownloadContentAsync("a"))!.Content.ToString());
        Assert.Equal("b", (await container.DownloadContentAsync("b"))!.Content.ToString());
    }

    [Fact]
    public async Task Lookups_ShouldFindNothing_WhenANameOrVersionIdHasU0000()
    {
        // Arrange: a provider whose storage can't hold U+0000 mustn't fail on a name or version ID that has it.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo a = await container.UploadAsync("a", BinaryData.FromString("a"));
        IResourceServiceClient service = ServiceOf(provider);
        string nulName = "a\0", nulVersionId = a.VersionId + "\0", nulContainer = container.Name + "\0";

        // Act & Assert
        Assert.False(await container.ExistsAsync(nulName));
        Assert.False(await container.ExistsAsync("a", nulVersionId));
        Assert.Null(await container.DownloadContentAsync(nulName));
        Assert.Empty(await container.GetResourceVersionsAsync(nulName));
        Assert.False(await container.SetContentTypeAsync(nulName, null, "text/plain"));
        Assert.False(await container.DeleteResourceIfExistsAsync(nulName));
        Assert.False(await container.DeleteResourceIfExistsAsync("a", nulVersionId));
        Assert.Empty(await container.ListResourcesAsync(new ResourceListOptions { Prefix = nulName }).ToListAsync());
        Assert.False(await service.ContainerExistsAsync(nulContainer));
        Assert.False(await service.DeleteContainerIfExistsAsync(nulContainer));
        Assert.Empty(await service.ListContainersAsync(new ContainerListOptions { Prefix = nulContainer }).ToListAsync());
        Assert.Equal("a", (await container.DownloadContentAsync("a"))!.Content.ToString());
    }

    public static TheoryData<string> InvalidContainerNames =>
        ["", " ", " leading", "trailing ", new string('c', ResourceNames.MaxContainerNameLength + 1)];

    public static TheoryData<string> InvalidResourceNames =>
        ["", " ", " leading", "trailing ", new string('r', ResourceNames.MaxResourceNameLength + 1)];

    [Theory]
    [MemberData(nameof(InvalidContainerNames))]
    public async Task CreateContainerAsync_ShouldThrowArgumentException_WhenGivenAnInvalidName(string name)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);

        // Act
        Task act() => service.CreateContainerAsync(name);

        // Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.DoesNotContain(await service.ListContainersAsync().ToListAsync(), c => c.Name == name);
    }

    [Theory]
    [MemberData(nameof(InvalidResourceNames))]
    public async Task UploadAsync_ShouldThrowArgumentException_WhenGivenAnInvalidName(string name)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Task act() => container.UploadAsync(name, BinaryData.FromString("x"));

        // Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.Empty(await container.ListResourcesAsync().ToListAsync());
    }
}
