using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task CreateResourceContainerAsync_ShouldCreateBoth_WhenNamesDifferOnlyInCase()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        var suffix = Guid.NewGuid().ToString("N");

        // Act
        await service.CreateResourceContainerAsync($"Docs-{suffix}");
        await service.CreateResourceContainerAsync($"docs-{suffix}");

        // Assert
        Assert.True(await service.ContainerExistsAsync($"Docs-{suffix}"));
        Assert.True(await service.ContainerExistsAsync($"docs-{suffix}"));
        Assert.False(await service.ContainerExistsAsync($"DOCS-{suffix}"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldKeepBothResources_WhenNamesDifferOnlyInCase()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        await container.UploadAsync("report.txt", BinaryData.FromString("lower"));
        await container.UploadAsync("Report.txt", BinaryData.FromString("upper"));

        // Assert
        Assert.Equal(2, (await container.GetResourcesAsync()).Count);
        Assert.Equal("lower", (await container.DownloadContentAsync("report.txt"))!.Content.ToString());
        Assert.Equal("upper", (await container.DownloadContentAsync("Report.txt"))!.Content.ToString());
        Assert.Null(await container.GetResourceInfoAsync("REPORT.TXT"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldRoundTripTheResource_WhenNamesAreAtTheMaximumLengths()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        var containerName = Guid.NewGuid().ToString("N").PadRight(ResourceNames.MaxContainerNameLength, 'c');
        var resourceName = new string('r', ResourceNames.MaxResourceNameLength);
        var payload = Encoding.UTF8.GetBytes("longest names");
        IResourceContainerClient container = await service.CreateResourceContainerAsync(containerName);

        // Act
        await container.UploadAsync(resourceName, new MemoryStream(payload));
        ResourceContent? content = await container.DownloadContentAsync(resourceName);

        // Assert
        Assert.Equal(payload, content!.Content.ToArray());
    }

    [SkippableFact]
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
            (await container.GetResourcesAsync()).Select(r => r.Name).Order(StringComparer.Ordinal));
        foreach (string name in names)
            Assert.Equal(name, (await container.DownloadContentAsync(name))!.Content.ToString());
    }

    [SkippableFact]
    public async Task CreateResourceContainerAsync_ShouldKeepContainersApart_WhenNamesLookLikePaths()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        var suffix = Guid.NewGuid().ToString("N");
        string[] names = [suffix, $"../{suffix}", $"x/../{suffix}", $"{suffix}/.."];

        // Act
        foreach (string name in names)
            await (await service.CreateResourceContainerAsync(name)).UploadAsync("r", BinaryData.FromString(name));

        // Assert
        foreach (string name in names)
        {
            Assert.True(await service.ContainerExistsAsync(name));
            ResourceContent? content = await service.GetResourceContainerClient(name).DownloadContentAsync("r");
            Assert.Equal(name, content!.Content.ToString());
        }
    }

    [SkippableFact]
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

    public static TheoryData<string> InvalidContainerNames =>
        ["", " ", " leading", "trailing ", new string('c', ResourceNames.MaxContainerNameLength + 1)];

    public static TheoryData<string> InvalidResourceNames =>
        ["", " ", " leading", "trailing ", new string('r', ResourceNames.MaxResourceNameLength + 1)];

    [SkippableTheory]
    [MemberData(nameof(InvalidContainerNames))]
    public async Task CreateResourceContainerAsync_ShouldThrowArgumentException_WhenGivenAnInvalidName(string name)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);

        // Act
        Task act() => service.CreateResourceContainerAsync(name);

        // Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(act);
        Assert.DoesNotContain(await service.GetResourceContainersAsync(), c => c.Name == name);
    }

    [SkippableTheory]
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
        Assert.Empty(await container.GetResourcesAsync());
    }
}
