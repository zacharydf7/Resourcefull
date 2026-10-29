using Moq;

namespace Resourcefull.Tests.Unit;

public class ResourceListingTests
{
    private const string Container = "containerName";

    private static IResourceContainerClient NewClient(IResourceRepository repository) =>
        new ResourceContainerClient(repository, null, new ResourcefullOptions(), new ResourceContainerInfo { Name = Container }, null);

    private static ResourceInfo Current(string name) => new() { ContainerName = Container, Name = name, VersionId = $"v-{name}", IsLatest = true };

    [Theory]
    [InlineData(0)]
    [InlineData(ResourceListOptions.MaxPageSize + 1)]
    public async Task GetResourcePageAsync_ShouldThrowArgumentOutOfRange_WhenGivenAPageSizeOutsideTheLimits(int pageSize)
    {
        // Arrange
        var repository = new Mock<IResourceRepository>(MockBehavior.Strict);

        // Act
        Task act() => NewClient(repository.Object).GetResourcePageAsync(new ResourceListOptions { PageSize = pageSize });

        // Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(act);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a token")]
    [InlineData("1.!!!.47DEQpj8HBQ")] // the right fingerprint for no prefix, so the name part is what's malformed
    [InlineData("2.YQ.AAAAAAAAAAA")]
    public async Task GetResourcePageAsync_ShouldThrowArgumentException_WhenGivenAMalformedToken(string token)
    {
        // Arrange
        var repository = new Mock<IResourceRepository>(MockBehavior.Strict);

        // Act
        Task act() => NewClient(repository.Object).GetResourcePageAsync(null, token);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>("continuationToken", act);
    }

    [Fact]
    public async Task GetResourcePageAsync_ShouldListEveryName_WhenGivenAnEmptyPrefix()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourcePageAsync(Container, null, null, 11, It.IsAny<CancellationToken>())).ReturnsAsync([Current("a")]);

        // Act
        ResourcePage page = await NewClient(repository.Object).GetResourcePageAsync(new ResourceListOptions { Prefix = "", PageSize = 10 });

        // Assert
        Assert.Equal("a", Assert.Single(page.Resources).Name);
    }

    [Fact]
    public async Task GetResourcePageAsync_ShouldFilterSortAndCutTheFullListing_WhenTheProviderWasWrittenFor30()
    {
        // Arrange: a provider written for 3.0 has only the full listing, in no particular order.
        var repository = new Mock<IResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetAllResourceInfoAsync(Container, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Current("p/d"), Current("q/a"), Current("p/B"), Current("p/a"), Current("p/c")]);

        // Act
        List<ResourceInfo> page = await repository.Object.GetResourcePageAsync(Container, "p/", "p/a", 2);

        // Assert
        Assert.Equal(["p/c", "p/d"], page.Select(r => r.Name));
    }

    [Fact]
    public async Task GetResourcePageAsync_ShouldThrowNotSupported_WhenAClientWrittenFor30IsAskedForAPage()
    {
        // Arrange
        IResourceContainerClient client = new Mock<IResourceContainerClient> { CallBase = true }.Object;

        // Act
        Task act() => client.GetResourcePageAsync();

        // Assert
        await Assert.ThrowsAsync<NotSupportedException>(act);
    }

    [Fact]
    public async Task ListResourcesAsync_ShouldFollowTheContinuationTokens_WhenTheListingHasSeveralPages()
    {
        // Arrange: an implementation that only provides pages gets the enumeration from the default implementation.
        var client = new Mock<IResourceContainerClient> { CallBase = true };
        client.Setup(c => c.GetResourcePageAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourcePage([Current("a"), Current("b")], "next"));
        client.Setup(c => c.GetResourcePageAsync(null, "next", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourcePage([Current("c")], null));

        // Act
        var names = new List<string>();
        await foreach (ResourceInfo resource in client.Object.ListResourcesAsync()) names.Add(resource.Name);

        // Assert
        Assert.Equal(["a", "b", "c"], names);
    }
}
