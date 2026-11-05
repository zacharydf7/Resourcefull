using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    private const string Customer = "customer";

    private ServiceProvider BuildSearchingServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null) =>
        BuildServiceProvider(o =>
        {
            o.UseMetadataSearch(Customer);
            configure?.Invoke(o);
        });

    private static ResourceUploadOptions ForCustomer(string customer, ResourceWriteMode mode = ResourceWriteMode.Create) =>
        new() { Mode = mode, Metadata = new Dictionary<string, string> { [Customer] = customer } };

    private static async Task<List<string>> FindAsync(IResourceContainerClient container, string customer, string? prefix = null) =>
        [.. (await container.ListResourcesAsync(new ResourceListOptions
        {
            Metadata = new ResourceMetadataMatch(Customer, customer), Prefix = prefix
        }).ToListAsync()).Select(r => r.Name)];

    [Fact]
    public async Task ListResourcesAsync_ShouldListOnlyCurrentVersionsWithTheValue_WhenGivenAMetadataMatch()
    {
        // Arrange: a and c are customer 42's; b's current version is customer 7's, though an older one was 42's.
        using ServiceProvider provider = BuildSearchingServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), ForCustomer("42"));
        await container.UploadAsync("b", BinaryData.FromString("b"), ForCustomer("42"));
        await container.UploadAsync("b", BinaryData.FromString("b2"), ForCustomer("7", ResourceWriteMode.Replace));
        await container.UploadAsync("c", BinaryData.FromString("c"), ForCustomer("42"));
        await container.UploadAsync("d", BinaryData.FromString("d"));
        await container.UploadAsync("e", BinaryData.FromString("e"), ForCustomer("420"));

        // Act
        List<string> found = await FindAsync(container, "42");
        List<string> prefixed = await FindAsync(container, "42", prefix: "c");

        // Assert
        Assert.Equal(["a", "c"], found);
        Assert.Equal(["c"], prefixed);
    }

    [Fact]
    public async Task GetResourcePageAsync_ShouldPageThroughTheMatches_WhenThereAreMoreThanAPage()
    {
        // Arrange
        using ServiceProvider provider = BuildSearchingServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        for (int i = 0; i < 5; i++) await container.UploadAsync($"m{i}", BinaryData.FromString("m"), ForCustomer("42"));
        for (int i = 0; i < 3; i++) await container.UploadAsync($"o{i}", BinaryData.FromString("o"), ForCustomer("7"));
        var options = new ResourceListOptions { Metadata = new ResourceMetadataMatch(Customer, "42"), PageSize = 2 };

        // Act
        var names = new List<string>();
        string? token = null;
        int pages = 0;
        do
        {
            ResourcePage page = await container.GetResourcePageAsync(options, token);
            names.AddRange(page.Resources.Select(r => r.Name));
            token = page.ContinuationToken;
            pages++;
        }
        while (token is not null);
        ResourcePage first = await container.GetResourcePageAsync(options);

        // Assert: a token can't be carried over to a search for another value.
        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], names);
        Assert.Equal(3, pages);
        await Assert.ThrowsAsync<ArgumentException>(() => container.GetResourcePageAsync(
            new ResourceListOptions { Metadata = new ResourceMetadataMatch(Customer, "7") }, first.ContinuationToken));
    }

    [Fact]
    public async Task ListResourcesAsync_ShouldThrowArgumentException_WhenTheKeyIsntSearchable()
    {
        // Arrange
        using ServiceProvider provider = BuildSearchingServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => container.ListResourcesAsync(new ResourceListOptions
        {
            Metadata = new ResourceMetadataMatch("region", "eu")
        }).ToListAsync().AsTask());
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowArgumentExceptionAndStoreNothing_WhenASearchableValueIsTooLong()
    {
        // Arrange
        using ServiceProvider provider = BuildSearchingServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string tooLong = new('x', ResourceMetadataMatch.MaxValueLength + 1);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => container.UploadAsync("a", BinaryData.FromString("a"), ForCustomer(tooLong)));
        Assert.False(await container.ExistsAsync("a"));
        await container.UploadAsync("b", BinaryData.FromString("b"),
            new ResourceUploadOptions { Metadata = new Dictionary<string, string> { ["notes"] = tooLong } });
    }

    [Fact]
    public async Task SetResourceMetadataAsync_ShouldMoveTheResourceToItsNewValue_WhenTheCurrentVersionsValueChanges()
    {
        Requires(SupportsVersionChanges, "changing a version's metadata");

        // Arrange
        using ServiceProvider provider = BuildSearchingServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), ForCustomer("42"));

        // Act
        await container.SetResourceMetadataAsync("a", null, new Dictionary<string, string> { [Customer] = "7" });

        // Assert
        Assert.Empty(await FindAsync(container, "42"));
        Assert.Equal(["a"], await FindAsync(container, "7"));
        await Assert.ThrowsAsync<ArgumentException>(() => container.SetResourceMetadataAsync("a", null,
            new Dictionary<string, string> { [Customer] = new string('x', ResourceMetadataMatch.MaxValueLength + 1) }));
    }

    [Fact]
    public async Task ListResourcesAsync_ShouldFindTheVersionThatBecomesCurrent_WhenTheCurrentOneIsDeleted()
    {
        // Arrange
        using ServiceProvider provider = BuildSearchingServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("1"), ForCustomer("42"));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("2"), ForCustomer("7", ResourceWriteMode.Replace));

        // Act
        await container.DeleteResourceIfExistsAsync("a", current.VersionId);

        // Assert
        Assert.Equal(["a"], await FindAsync(container, "42"));
        Assert.Empty(await FindAsync(container, "7"));
    }

    [Fact]
    public async Task ListResourcesAsync_ShouldFollowTheResource_WhenItsRenamedRestoredCopiedAndDeleted()
    {
        Requires(SupportsMove, "moves");

        // Arrange
        using ServiceProvider provider = BuildSearchingServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        IResourceContainerClient other = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("a", BinaryData.FromString("1"), ForCustomer("42"));

        // Act
        await container.RenameResourceAsync("a", "b");
        List<string> afterRename = await FindAsync(container, "42");
        await container.UploadAsync("b", BinaryData.FromString("2"), ForCustomer("7", ResourceWriteMode.Replace));
        await container.RestoreVersionAsync("b", first.VersionId);
        List<string> afterRestore = await FindAsync(container, "42");
        await container.CopyResourceAsync("b", other.Name, "c");
        await container.MoveResourceAsync("b", other.Name, "d");
        List<string> left = await FindAsync(container, "42");
        List<string> inOther = await FindAsync(other, "42");
        await other.DeleteResourceIfExistsAsync("d");

        // Assert
        Assert.Equal(["b"], afterRename);
        Assert.Equal(["b"], afterRestore);
        Assert.Empty(left);
        Assert.Equal(["c", "d"], inOther);
        Assert.Equal(["c"], await FindAsync(other, "42"));
    }

    [Fact]
    public async Task ListResourcesAsync_ShouldMatchTheValueExactly_WhenValuesDifferOnlyByTrailingSpaces()
    {
        // Arrange
        using ServiceProvider provider = BuildSearchingServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("a"), ForCustomer("42 "));
        await container.UploadAsync("b", BinaryData.FromString("b"), ForCustomer("42"));
        await container.UploadAsync("c", BinaryData.FromString("c"), ForCustomer("42"));

        // Act: a page at a time, so a page can come back short when the database matches a name whose value isn't an exact
        // match.
        List<string> paged = [.. (await container.ListResourcesAsync(new ResourceListOptions
        {
            Metadata = new ResourceMetadataMatch(Customer, "42"), PageSize = 1
        }).ToListAsync()).Select(r => r.Name)];

        // Assert
        Assert.Equal(["b", "c"], paged);
        Assert.Equal(["a"], await FindAsync(container, "42 "));
    }

    [Fact]
    public async Task IndexMetadataAsync_ShouldLeaveOutAValueTooLongToSearchFor_WhenItWasStoredBeforeTheKeyWasNamed()
    {
        Assert.SkipUnless(SharesStoreBetweenServiceProviders, "Each service provider has a store of its own.");
        Requires(SupportsMove, "moves");

        // Arrange: stored by an application that didn't search by the key, so nothing limited its value.
        using ServiceProvider before = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient stored = await NewContainerAsync(before);
        string tooLong = new('x', ResourceMetadataMatch.MaxValueLength + 1);
        await stored.UploadAsync("a", BinaryData.FromString("1"), ForCustomer(tooLong));
        ResourceInfo current = await stored.UploadAsync("a", BinaryData.FromString("2"), ForCustomer("42", ResourceWriteMode.Replace));
        using ServiceProvider after = BuildSearchingServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = ServiceOf(after).GetContainerClient(stored.Name);

        // Act: indexing, then writes that make the version with the long value current, and move it.
        await ServiceOf(after).GetContainerClient(stored.Name).IndexMetadataAsync();
        await container.DeleteResourceIfExistsAsync("a", current.VersionId);
        await container.RenameResourceAsync("a", "b");

        // Assert: it's kept, and no search can look for it.
        Assert.True(await container.ExistsAsync("b"));
        Assert.Empty(await FindAsync(container, "42"));
        await Assert.ThrowsAsync<ArgumentException>(() => FindAsync(container, tooLong));
    }

    [Fact]
    public async Task IndexMetadataAsync_ShouldMakeVersionsStoredBeforeAKeyWasAddedFindable_WhenCalled()
    {
        Assert.SkipUnless(SharesStoreBetweenServiceProviders, "Each service provider has a store of its own.");

        // Arrange: stored by an application that didn't search by the key yet.
        using ServiceProvider before = BuildServiceProvider();
        IResourceContainerClient stored = await NewContainerAsync(before);
        await stored.UploadAsync("a", BinaryData.FromString("a"), ForCustomer("42"));
        using ServiceProvider after = BuildSearchingServiceProvider();
        IResourceContainerClient container = ServiceOf(after).GetContainerClient(stored.Name);

        // Act
        await ServiceOf(after).GetContainerClient(stored.Name).IndexMetadataAsync();

        // Assert
        Assert.Equal(["a"], await FindAsync(container, "42"));
    }
}
