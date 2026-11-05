using Moq;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

/// <summary>Range downloads through providers and clients written before ranges existed.</summary>
public class ResourceRangeTests
{
    private const string Container = "c";

    private static readonly byte[] Payload = [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)];

    /// <summary>A provider that opens only whole content, so the range overload is the default one.</summary>
    private static Mock<ResourceRepository> ProviderWithoutRanges()
    {
        var repository = new Mock<ResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceLocation(Container, "r"), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "r", VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(new ResourceLocation(Container, "r"), "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Payload, writable: false));
        return repository;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_000)]
    [InlineData(200_000)]
    public async Task GetResourceStreamAsync_ShouldPositionTheWholeContentAtTheOffset_WhenTheProviderDoesNotOverrideTheRangeOverload(long offset)
    {
        // Arrange
        ResourceRepository repository = ProviderWithoutRanges().Object;

        // Act
        await using Stream stream = (await repository.GetResourceStreamAsync(new ResourceLocation(Container, "r"), "v1", offset, 10))!;

        // Assert
        Assert.Equal(offset, stream.Position);
        Assert.Equal(Payload.Length, stream.Length);
        Assert.Equal(offset < Payload.Length ? Payload[offset] : -1, stream.ReadByte());
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldReturnNull_WhenTheProviderDoesNotOverrideTheRangeOverloadAndTheVersionDoesNotExist()
    {
        // Arrange
        ResourceRepository repository = ProviderWithoutRanges().Object;

        // Act
        Stream? stream = await repository.GetResourceStreamAsync(new ResourceLocation(Container, "r"), "missing", 0, 10);

        // Assert
        Assert.Null(stream);
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldStopAtTheRangesEnd_WhenTheProvidersStreamGoesOn()
    {
        // Arrange: the default range overload reads on to the end of the content.
        var client = new ResourceContainerClient(ProviderWithoutRanges().Object, null, new ResourcefullOptions(),
            Container, null);

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
}
