using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task GetVersionPageAsync_ShouldListEveryVersionOfEachNameInOrder_WhenReadAPageAtATime()
    {
        // Arrange: three names, the middle one with three versions.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await UploadAllAsync(container, "c", "a");
        var versionsOfB = new List<string>();
        for (int i = 0; i < 3; i++)
            versionsOfB.Add((await container.UploadAsync("b", BinaryData.FromString($"b{i}"))).VersionId);
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        string containerName = container.ResourceContainerInfo.Name;

        // Act
        List<ResourceInfo> first = await repository.GetVersionPageAsync(containerName, null, 2);
        List<ResourceInfo> second = await repository.GetVersionPageAsync(containerName, "b", 2);
        List<ResourceInfo> past = await repository.GetVersionPageAsync(containerName, "c", 2);

        // Assert: the names in order, and each name's versions current first, then newest to oldest.
        Assert.Equal(["a", "b", "b", "b"], first.Select(v => v.Name));
        Assert.Equal([versionsOfB[2], versionsOfB[1], versionsOfB[0]], first.Skip(1).Select(v => v.VersionId));
        Assert.True(first[1].IsLatest);
        Assert.Equal(["c"], second.Select(v => v.Name));
        Assert.Empty(past);
    }

    private static async Task UploadAllAsync(IResourceContainerClient container, params string[] names)
    {
        foreach (string name in names) await container.UploadAsync(name, BinaryData.FromString(name));
    }

    private static async Task<List<string>> ListNamesAsync(IResourceContainerClient container, ResourceListOptions? options = null)
    {
        var names = new List<string>();
        await foreach (ResourceInfo resource in container.ListResourcesAsync(options)) names.Add(resource.Name);
        return names;
    }

    [SkippableFact]
    public async Task ListResourcesAsync_ShouldReturnEachCurrentResourceOnceInOrdinalOrder_WhenTheyFillSeveralPages()
    {
        // Arrange: one name with several versions, which lists once, as its current version.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await UploadAllAsync(container, "e", "b", "g", "a", "d", "c", "f");
        ResourceInfo current = await container.UploadAsync("d", BinaryData.FromString("newer"));

        // Act
        var resources = new List<ResourceInfo>();
        await foreach (ResourceInfo resource in container.ListResourcesAsync(new ResourceListOptions { PageSize = 3 })) resources.Add(resource);

        // Assert
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], resources.Select(r => r.Name));
        Assert.All(resources, r => Assert.True(r.IsLatest));
        Assert.Equal(current.VersionId, resources.Single(r => r.Name == "d").VersionId);
    }

    [SkippableFact]
    public async Task ListResourcesAsync_ShouldOrderNamesByOrdinalComparison_WhenTheyHoldAnyCharacters()
    {
        // Arrange: case, accents, a character near the top of the Basic Multilingual Plane and one beyond it (stored as
        // a surrogate pair, which sorts below U+FFFD code unit by code unit).
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string[] names = ["b", "B", "a", "é", "�", "\U0001F600", "Z", "z", "1"];
        await UploadAllAsync(container, names);

        // Act
        List<string> listed = await ListNamesAsync(container, new ResourceListOptions { PageSize = 2 });

        // Assert
        Assert.Equal(names.Order(StringComparer.Ordinal), listed);
    }

    [SkippableFact]
    public async Task ListResourcesAsync_ShouldReturnOnlyNamesThatStartWithThePrefix_WhenGivenAPrefix()
    {
        // Arrange: the prefix is matched ordinally, case-sensitively, and with its characters taken literally.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await UploadAllAsync(container, "2026/10/a", "2026/10/b", "2026/1/c", "2026/100", "2026/10", "2026/10/C", "2025/10/a");

        // Act
        List<string> listed = await ListNamesAsync(container, new ResourceListOptions { Prefix = "2026/10/", PageSize = 1 });

        // Assert
        Assert.Equal(["2026/10/C", "2026/10/a", "2026/10/b"], listed);
    }

    [SkippableFact]
    public async Task ListResourcesAsync_ShouldMatchWildcardCharactersLiterally_WhenThePrefixHoldsThem()
    {
        // Arrange: characters that are wildcards in SQL's LIKE.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await UploadAllAsync(container, "a%b", "axb", "a_b", "a[b]", "ab", @"a\b");

        // Act & Assert
        Assert.Equal(["a%b"], await ListNamesAsync(container, new ResourceListOptions { Prefix = "a%" }));
        Assert.Equal(["a_b"], await ListNamesAsync(container, new ResourceListOptions { Prefix = "a_" }));
        Assert.Equal(["a[b]"], await ListNamesAsync(container, new ResourceListOptions { Prefix = "a[" }));
        Assert.Equal([@"a\b"], await ListNamesAsync(container, new ResourceListOptions { Prefix = @"a\" }));
    }

    [SkippableFact]
    public async Task ListResourcesAsync_ShouldMatchTheWholePrefix_WhenThePrefixIsAsLongAsTheLongestNameOrLonger()
    {
        // Arrange: a prefix escaped for SQL's LIKE grows to about twice its length, past the name column's size.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string longest = new('_', ResourceNames.MaxResourceNameLength);
        await UploadAllAsync(container, longest, new string('_', ResourceNames.MaxResourceNameLength - 1) + "a");

        // Act & Assert
        Assert.Equal([longest], await ListNamesAsync(container, new ResourceListOptions { Prefix = longest }));
        Assert.Empty(await ListNamesAsync(container, new ResourceListOptions { Prefix = longest + "_" }));
    }

    [SkippableFact]
    public async Task GetResourcePageAsync_ShouldReturnNoContinuationToken_WhenThePageIsTheLast()
    {
        // Arrange: names that fill the pages exactly, so the last page is full.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await UploadAllAsync(container, "a", "b", "c", "d");
        var options = new ResourceListOptions { PageSize = 2 };

        // Act
        ResourcePage first = await container.GetResourcePageAsync(options);
        ResourcePage second = await container.GetResourcePageAsync(options, first.ContinuationToken);

        // Assert
        Assert.Equal(["a", "b"], first.Resources.Select(r => r.Name));
        Assert.NotNull(first.ContinuationToken);
        Assert.Equal(["c", "d"], second.Resources.Select(r => r.Name));
        Assert.Null(second.ContinuationToken);
    }

    [SkippableFact]
    public async Task GetResourcePageAsync_ShouldContinueAfterTheLastName_WhenThatNameIsDeletedBetweenPages()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await UploadAllAsync(container, "a", "b", "c", "d");
        var options = new ResourceListOptions { PageSize = 2 };
        ResourcePage first = await container.GetResourcePageAsync(options);
        await container.DeleteResourceIfExistsAsync("b");

        // Act
        ResourcePage second = await container.GetResourcePageAsync(options, first.ContinuationToken);

        // Assert
        Assert.Equal(["c", "d"], second.Resources.Select(r => r.Name));
    }

    [SkippableFact]
    public async Task GetResourcePageAsync_ShouldReturnAnEmptyPage_WhenTheContainerDoesNotExist()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = ServiceOf(provider).GetResourceContainerClient(NewContainerName());

        // Act
        ResourcePage page = await container.GetResourcePageAsync();

        // Assert
        Assert.Empty(page.Resources);
        Assert.Null(page.ContinuationToken);
    }

    [SkippableFact]
    public async Task GetResourcePageAsync_ShouldThrowArgumentException_WhenTheTokenComesFromAListingOfAnotherPrefix()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await UploadAllAsync(container, "a/1", "a/2");
        ResourcePage first = await container.GetResourcePageAsync(new ResourceListOptions { Prefix = "a/", PageSize = 1 });

        // Act
        Task act() => container.GetResourcePageAsync(new ResourceListOptions { Prefix = "b/" }, first.ContinuationToken);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>("continuationToken", act);
    }
}
