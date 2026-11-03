using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    // A store's other tests leave containers behind, so each listing here is of a prefix only its own test uses.
    private static async Task<List<ContainerInfo>> ListContainersAsync(IResourceServiceClient service, string prefix,
        int pageSize = ContainerListOptions.MaxPageSize) =>
        await service.ListContainersAsync(new ContainerListOptions { Prefix = prefix, PageSize = pageSize }).ToListAsync();

    private static async Task CreateAllAsync(IResourceServiceClient service, string prefix, params string[] suffixes)
    {
        foreach (string suffix in suffixes) await service.CreateContainerAsync(prefix + suffix);
    }

    [Fact]
    public async Task ListContainersAsync_ShouldReturnEachContainerOnceInOrdinalOrder_WhenTheyFillSeveralPages()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string prefix = NewContainerName();
        string[] suffixes = ["e", "B", "g", "a", "d", "b", "f", "1"];
        await CreateAllAsync(service, prefix, suffixes);

        // Act
        List<ContainerInfo> listed = await ListContainersAsync(service, prefix, pageSize: 3);

        // Assert
        Assert.Equal(suffixes.Select(suffix => prefix + suffix).Order(StringComparer.Ordinal), listed.Select(c => c.Name));
    }

    [Fact]
    public async Task ListContainersAsync_ShouldReturnOnlyNamesThatStartWithThePrefix_WhenGivenAPrefix()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string prefix = NewContainerName();
        await CreateAllAsync(service, prefix, "-user-1", "-user-2", "-team-1", "_user-3");

        // Act
        List<ContainerInfo> listed = await ListContainersAsync(service, prefix + "-user-");

        // Assert
        Assert.Equal([prefix + "-user-1", prefix + "-user-2"], listed.Select(c => c.Name));
    }

    [Fact]
    public async Task GetContainerPageAsync_ShouldReturnNoContinuationToken_WhenThePageIsTheLast()
    {
        // Arrange: containers that fill the pages exactly, so the last page is full.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string prefix = NewContainerName();
        await CreateAllAsync(service, prefix, "a", "b", "c", "d");
        var options = new ContainerListOptions { Prefix = prefix, PageSize = 2 };

        // Act
        ContainerPage first = await service.GetContainerPageAsync(options);
        ContainerPage second = await service.GetContainerPageAsync(options, first.ContinuationToken);

        // Assert
        Assert.Equal([prefix + "a", prefix + "b"], first.Containers.Select(c => c.Name));
        Assert.NotNull(first.ContinuationToken);
        Assert.Equal([prefix + "c", prefix + "d"], second.Containers.Select(c => c.Name));
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task GetContainerPageAsync_ShouldContinueAfterTheLastName_WhenThatContainerIsDeletedBetweenPages()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string prefix = NewContainerName();
        await CreateAllAsync(service, prefix, "a", "b", "c", "d");
        var options = new ContainerListOptions { Prefix = prefix, PageSize = 2 };
        ContainerPage first = await service.GetContainerPageAsync(options);
        await service.DeleteContainerIfExistsAsync(prefix + "b");

        // Act
        ContainerPage second = await service.GetContainerPageAsync(options, first.ContinuationToken);

        // Assert
        Assert.Equal([prefix + "c", prefix + "d"], second.Containers.Select(c => c.Name));
    }

    [Fact]
    public async Task GetContainerPageAsync_ShouldThrowArgumentException_WhenTheTokenComesFromAListingOfAnotherPrefix()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string prefix = NewContainerName();
        await CreateAllAsync(service, prefix, "a", "b");
        ContainerPage first = await service.GetContainerPageAsync(new ContainerListOptions { Prefix = prefix, PageSize = 1 });

        // Act
        Task act() => service.GetContainerPageAsync(new ContainerListOptions { Prefix = prefix + "a" }, first.ContinuationToken);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>("continuationToken", act);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ContainerListOptions.MaxPageSize + 1)]
    public async Task GetContainerPageAsync_ShouldThrowArgumentOutOfRangeException_WhenThePageSizeIsOutOfRange(int pageSize)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();

        // Act
        Task act() => ServiceOf(provider).GetContainerPageAsync(new ContainerListOptions { PageSize = pageSize });

        // Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("options", act);
    }
}
