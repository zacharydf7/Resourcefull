using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    [Fact]
    public async Task GetTopVersionsAsync_ShouldListTheLargestVersionsFirst_WhenGivenLargestFirst()
    {
        // Arrange: an older version among the current ones, and two of the same size.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo older = await container.UploadAsync("a", BinaryData.FromString("123"));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("1234567890"),
            new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        ResourceInfo b = await container.UploadAsync("b", BinaryData.FromString("12345"));
        ResourceInfo d = await container.UploadAsync("d", BinaryData.FromString("abcde"));
        await container.UploadAsync("c", BinaryData.FromString("1"));

        // Act
        IReadOnlyList<ResourceInfo> top = await container.GetTopVersionsAsync(ResourceVersionOrder.LargestFirst, 4);

        // Assert
        Assert.Equal([current.VersionId, .. new[] { b.VersionId, d.VersionId }.Order(StringComparer.Ordinal), older.VersionId],
            top.Select(v => v.VersionId));
        Assert.Equal([true, true, true, false], top.Select(v => v.IsLatest));
        Assert.Equal(10, top[0].ContentLength);
    }

    [Fact]
    public async Task GetTopVersionsAsync_ShouldListTheVersionsThatExpireSoonestFirst_WhenGivenExpiringFirst()
    {
        Requires(SupportsExpiry, "expiry");

        // Arrange: an older version that expires last, and a name that never expires. The times are in the future, so
        // another test's sweep doesn't delete them.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo older = await container.UploadAsync("a", BinaryData.FromString("1"), Expiring(Future.AddHours(2)));
        ResourceInfo current = await container.UploadAsync("a", BinaryData.FromString("2"), Expiring(Future));
        ResourceInfo b = await container.UploadAsync("b", BinaryData.FromString("b"), Expiring(Future.AddHours(1)));
        await container.UploadAsync("c", BinaryData.FromString("c"));

        // Act
        IReadOnlyList<ResourceInfo> top = await container.GetTopVersionsAsync(ResourceVersionOrder.ExpiringFirst);

        // Assert
        Assert.Equal([current.VersionId, b.VersionId, older.VersionId], top.Select(v => v.VersionId));
        Assert.Equal([true, true, false], top.Select(v => v.IsLatest));
    }

    [Fact]
    public async Task GetTopVersionsAsync_ShouldReturnNothing_WhenTheContainerDoesNotExist()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = ServiceOf(provider).GetContainerClient(NewContainerName());

        // Act
        IReadOnlyList<ResourceInfo> top = await container.GetTopVersionsAsync(ResourceVersionOrder.LargestFirst);

        // Assert
        Assert.Empty(top);
    }

    [Fact]
    public async Task GetExpiredVersionsAsync_ShouldListExpiredVersionsFirstExpiredFirst_WhenSomeHaveExpired()
    {
        Requires(SupportsExpiry, "expiry");

        // Arrange: versions that expire tomorrow, read by a clock an hour after that, so another test's sweep doesn't
        // delete them; one that expires later, and one that never expires.
        var clock = new ManualTimeProvider { Now = Future.AddHours(1) };
        using ServiceProvider provider = BuildServiceProvider(o => o.Services.AddSingleton<TimeProvider>(clock));
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo a = await container.UploadAsync("a", BinaryData.FromString("a"), Expiring(Future.AddMinutes(10)));
        ResourceInfo b = await container.UploadAsync("b", BinaryData.FromString("b"), Expiring(Future.AddMinutes(5)));
        await container.UploadAsync("c", BinaryData.FromString("c"), Expiring(Future.AddHours(2)));
        await container.UploadAsync("d", BinaryData.FromString("d"));

        // Act
        IReadOnlyList<ResourceInfo> expired = await ServiceOf(provider).GetExpiredVersionsAsync(1000);

        // Assert: other tests' versions may be listed too.
        Assert.Equal([b.VersionId, a.VersionId], expired.Where(v => v.ContainerName == container.Name).Select(v => v.VersionId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task GetExpiredVersionsAsync_ShouldThrowArgumentOutOfRange_WhenGivenACountOutsideTheLimits(int maxVersions)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();

        // Act
        Task act() => ServiceOf(provider).GetExpiredVersionsAsync(maxVersions);

        // Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(act);
    }
}
