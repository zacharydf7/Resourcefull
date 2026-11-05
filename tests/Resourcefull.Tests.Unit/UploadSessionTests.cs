using Moq;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

public class UploadSessionTests
{
    [Fact]
    public async Task CompleteUploadAsync_ShouldThrowAndCompleteNothing_WhenAnAppendLandsWhileTheContentIsMeasured()
    {
        // Arrange: the session had 3 bytes when its checks ran, and 5 by the time its content was read.
        var repository = new Mock<ResourceRepository>();
        var location = new ResourceLocation("c", "a");
        repository.Setup(r => r.GetStagedVersionAsync(location, "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StagedVersion(new NewResourceVersion(location, "u1") { DeclaredLength = 3 }, 3));
        repository.Setup(r => r.GetStagedStreamAsync(location, "u1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[5]));
        var container = new ResourceContainerClient(repository.Object, null, new ResourcefullOptions(), "c", null);

        // Act
        var exception = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() => container.CompleteUploadAsync("a", "u1"));

        // Assert
        Assert.Equal(5, exception.CurrentOffset);
        repository.Verify(r => r.CompleteStagedVersionAsync(It.IsAny<ResourceLocation>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<ResourceWriteCondition?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
