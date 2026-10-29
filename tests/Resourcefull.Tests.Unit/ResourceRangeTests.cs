using Moq;

namespace Resourcefull.Tests.Unit;

/// <summary>Range downloads through providers and clients written before ranges existed.</summary>
public class ResourceRangeTests
{
    private const string Container = "c";

    private static readonly byte[] Payload = [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)];

    /// <summary>A provider written for 3.1: it opens whole content, and the range overload is the default one.</summary>
    private static Mock<IResourceRepository> ProviderWrittenFor31()
    {
        var repository = new Mock<IResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetResourceInfoAsync(Container, "r", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "r", VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, "r", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Payload, writable: false));
        return repository;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_000)]
    [InlineData(200_000)]
    public async Task GetResourceStreamAsync_ShouldPositionTheWholeContentAtTheOffset_WhenAProviderWrittenFor31OpensARange(long offset)
    {
        // Arrange
        IResourceRepository repository = ProviderWrittenFor31().Object;

        // Act
        await using Stream stream = (await repository.GetResourceStreamAsync(Container, "r", "v1", offset, 10))!;

        // Assert
        Assert.Equal(offset, stream.Position);
        Assert.Equal(Payload.Length, stream.Length);
        Assert.Equal(offset < Payload.Length ? Payload[offset] : -1, stream.ReadByte());
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldReturnNull_WhenAProviderWrittenFor31OpensARangeOfAVersionThatDoesNotExist()
    {
        // Arrange
        IResourceRepository repository = ProviderWrittenFor31().Object;

        // Act
        Stream? stream = await repository.GetResourceStreamAsync(Container, "r", "missing", 0, 10);

        // Assert
        Assert.Null(stream);
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldStopAtTheRangesEnd_WhenTheProvidersStreamGoesOn()
    {
        // Arrange: the default range overload reads on to the end of the content.
        var client = new ResourceContainerClient(ProviderWrittenFor31().Object, null, new ResourcefullOptions(),
            new ResourceContainerInfo { Name = Container }, null);

        // Act
        ResourceDownloadResult? result = await client.DownloadStreamingAsync("r", null, new ResourceRange(100_000, 1_000), CancellationToken.None);
        await using Stream stream = result!.Stream;
        var read = new MemoryStream();
        await stream.CopyToAsync(read);

        // Assert
        Assert.Equal(Payload[100_000..101_000], read.ToArray());
        Assert.Equal(new ResourceRange(100_000, 1_000), result.Range);
        Assert.Equal(1_000, stream.Length);
        Assert.Equal(1_000, stream.Position);
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldThrowNotSupported_WhenAClientWrittenFor31IsGivenARange()
    {
        // Arrange
        IResourceContainerClient client = new Mock<IResourceContainerClient> { CallBase = true }.Object;

        // Act
        Task act() => client.DownloadStreamingAsync("r", null, new ResourceRange(0, 10), CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<NotSupportedException>(act);
    }
}
