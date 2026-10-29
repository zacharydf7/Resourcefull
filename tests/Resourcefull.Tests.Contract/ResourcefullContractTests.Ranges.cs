using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    // Several chunks long, in providers that store content in pieces of 64 KiB.
    private const int RangedLength = 150_000;

    public static TheoryData<long, long?, long> Ranges => new()
    {
        { 0, 10, 10 },
        { 65_530, 20, 20 },          // across a chunk boundary
        { 65_535, 65_535, 65_535 },  // exactly the second chunk
        { 100_000, null, 50_000 },   // to the end
        { 149_990, 100, 10 },        // past the end, so cut there
        { 0, null, RangedLength },   // the whole content
        { 5, 0, 0 },
        { RangedLength, null, 0 }    // starting at the end
    };

    [SkippableTheory]
    [MemberData(nameof(Ranges))]
    public async Task DownloadStreamingAsync_ShouldReturnOnlyTheRange_WhenGivenARange(long offset, long? length, long expectedLength)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        byte[] payload = RandomNumberGenerator.GetBytes(RangedLength);
        await container.UploadAsync("video.mp4", new MemoryStream(payload));

        // Act
        ResourceDownloadResult? result = await container.DownloadStreamingAsync("video.mp4", null, new ResourceRange(offset, length), CancellationToken.None);
        await using Stream stream = result!.Stream;
        var read = new MemoryStream();
        await stream.CopyToAsync(read);

        // Assert
        Assert.Equal(payload.AsSpan((int)offset, (int)expectedLength).ToArray(), read.ToArray());
        Assert.Equal(new ResourceRange(offset, expectedLength), result.Range);
        Assert.Equal(RangedLength, result.TotalLength);
        Assert.Equal(expectedLength, stream.Length);
        Assert.Equal(expectedLength, stream.Position);
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldReportTheWholeLengthAndNoRange_WhenGivenNoRange()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("report.txt", BinaryData.FromString("hello"));

        // Act
        ResourceDownloadResult? result = await container.DownloadStreamingAsync("report.txt");
        await using Stream stream = result!.Stream;

        // Assert
        Assert.Equal(5, result.TotalLength);
        Assert.Null(result.Range);
    }

    [SkippableTheory]
    [InlineData(RangedLength + 1, null)]
    [InlineData(-1, 10L)]
    [InlineData(0, -1L)]
    public async Task DownloadStreamingAsync_ShouldThrowArgumentOutOfRange_WhenTheRangeStartsPastTheEndOrIsNegative(long offset, long? length)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("video.mp4", new MemoryStream(new byte[RangedLength]));

        // Act
        Task act() => container.DownloadStreamingAsync("video.mp4", null, new ResourceRange(offset, length), CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(act);
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldReturnNull_WhenGivenARangeOfAResourceThatDoesNotExist()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        ResourceDownloadResult? result = await container.DownloadStreamingAsync("missing", null, new ResourceRange(0, 10), CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldReadTheRangeOfTheGivenVersion_WhenANewerOneWasUploaded()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("notes.txt", BinaryData.FromString("first version"));
        await container.UploadAsync("notes.txt", BinaryData.FromString("second version"));

        // Act
        ResourceDownloadResult? result = await container.DownloadStreamingAsync("notes.txt", first.VersionId, new ResourceRange(0, 5), CancellationToken.None);
        await using Stream stream = result!.Stream;

        // Assert
        Assert.Equal("first", await new StreamReader(stream).ReadToEndAsync());
        Assert.Equal(first.VersionId, result.ResourceInfo.VersionId);
        Assert.Equal(13, result.TotalLength);
    }
}
