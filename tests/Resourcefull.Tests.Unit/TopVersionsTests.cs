using Moq;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

public class TopVersionsTests
{
    private const string Container = "containerName";

    private static IResourceContainerClient NewClient(ResourceRepository repository) =>
        new ResourceContainerClient(repository, null, new ResourcefullOptions(), new ContainerInfo { Name = Container }, null);

    private static ResourceInfo Version(string name, string versionId, long? length, DateTimeOffset? expiresAt = null) =>
        new() { ContainerName = Container, Name = name, VersionId = versionId, ContentLength = length, ExpiresAt = expiresAt };

    [Theory]
    [InlineData(ResourceVersionOrder.LargestFirst, 0)]
    [InlineData(ResourceVersionOrder.LargestFirst, 1001)]
    [InlineData((ResourceVersionOrder)2, 20)]
    public async Task GetTopVersionsAsync_ShouldThrowArgumentOutOfRange_WhenGivenAnOrderOrCountOutsideTheLimits(
        ResourceVersionOrder order, int count)
    {
        // Arrange
        var repository = new Mock<ResourceRepository>(MockBehavior.Strict);

        // Act
        Task act() => NewClient(repository.Object).GetTopVersionsAsync(order, count);

        // Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(act);
    }

    /// <summary>A provider that answers only pages of versions: these two, then nothing.</summary>
    private static Mock<ResourceRepository> PagedRepository(IReadOnlyList<ResourceInfo> first, IReadOnlyList<ResourceInfo> second)
    {
        string firstLast = first[^1].Name, secondLast = second[^1].Name;
        var repository = new Mock<ResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetVersionPageAsync(Container, null, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(first);
        repository.Setup(r => r.GetVersionPageAsync(Container, firstLast, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(second);
        repository.Setup(r => r.GetVersionPageAsync(Container, secondLast, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return repository;
    }

    [Fact]
    public async Task GetTopVersionsAsync_ShouldKeepTheLargestFromEveryPage_WhenTheProviderDoesNotOverrideIt()
    {
        // Arrange: the largest version on the second page, a tie broken by version ID, and a version without a length.
        Mock<ResourceRepository> repository = PagedRepository(
            [Version("a", "v2", 5), Version("a", "v1", null), Version("b", "v4", 3)],
            [Version("c", "v6", 9), Version("d", "v3", 5), Version("d", "v5", 1)]);

        // Act
        IReadOnlyList<ResourceInfo> top = await repository.Object.GetTopVersionsAsync(Container, ResourceVersionOrder.LargestFirst, 4);

        // Assert
        Assert.Equal(["v6", "v2", "v3", "v4"], top.Select(v => v.VersionId));
    }

    [Fact]
    public async Task GetTopVersionsAsync_ShouldListAVersionWithoutALengthLast_WhenTheProviderDoesNotOverrideIt()
    {
        // Arrange
        Mock<ResourceRepository> repository = PagedRepository([Version("a", "v1", null), Version("a", "v2", 0)], [Version("b", "v3", 2)]);

        // Act
        IReadOnlyList<ResourceInfo> top = await repository.Object.GetTopVersionsAsync(Container, ResourceVersionOrder.LargestFirst, 10);

        // Assert
        Assert.Equal(["v3", "v2", "v1"], top.Select(v => v.VersionId));
    }

    [Fact]
    public async Task GetTopVersionsAsync_ShouldListOnlyVersionsThatExpireSoonestFirst_WhenGivenExpiringFirst()
    {
        // Arrange
        var start = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        Mock<ResourceRepository> repository = PagedRepository(
            [Version("a", "v1", 1, start.AddHours(2)), Version("a", "v2", 1), Version("b", "v4", 1, start.AddHours(1))],
            [Version("c", "v3", 1, start.AddHours(1)), Version("d", "v5", 1, start.AddHours(3))]);

        // Act
        IReadOnlyList<ResourceInfo> top = await repository.Object.GetTopVersionsAsync(Container, ResourceVersionOrder.ExpiringFirst, 3);

        // Assert
        Assert.Equal(["v3", "v4", "v1"], top.Select(v => v.VersionId));
    }
}
