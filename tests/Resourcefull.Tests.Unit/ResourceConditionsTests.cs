using Moq;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

public class ResourceConditionsTests
{
    private const string Container = "containerName";

    private static readonly WrittenContent Written = new(1, "sha256:written", DateTimeOffset.UnixEpoch);

    private static ResourceContainerClient NewClient(ResourceRepository repository, bool versioning = false) =>
        new(repository, null, new ResourcefullOptions { MaxVersions = versioning ? 100 : 1 }, new ContainerInfo { Name = Container }, null);

    private static Mock<ResourceRepository> NewRepository()
    {
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetResourceVersionsAsync(It.Is<ResourceLocation>(l => l.ContainerName == Container), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(r => r.AddResourceAsync(It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<ResourceWriteCondition?>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(Written);
        return repository;
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowArgumentException_WhenGivenConditionsInCreateMode()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>(MockBehavior.Strict);
        var options = new ResourceUploadOptions { Mode = ResourceWriteMode.Create, Conditions = new ResourceConditions { IfMatchVersionId = "v1" } };

        // Act
        Task act() => NewClient(repository.Object).UploadAsync("r", new MemoryStream(), options);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(act);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldThrowArgumentException_WhenGivenDestinationConditionsInCreateMode()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>(MockBehavior.Strict);
        var options = new ResourceCopyOptions { Mode = ResourceWriteMode.Create, DestinationConditions = new ResourceConditions { IfMatchVersionId = "v1" } };

        // Act
        Task act() => NewClient(repository.Object).CopyResourceAsync("r", Container, "copy", options);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(act);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadAndCopy_ShouldThrowOutOfRange_WhenGivenAnUndefinedMode()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>(MockBehavior.Strict);
        var mode = (ResourceWriteMode)5;

        // Act
        Task upload() => NewClient(repository.Object).UploadAsync("r", new MemoryStream(), new ResourceUploadOptions { Mode = mode });
        Task copy() => NewClient(repository.Object).CopyResourceAsync("r", Container, "copy", new ResourceCopyOptions { Mode = mode });

        // Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(upload);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(copy);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldCopyCreateOnly_WhenGivenNoMode()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceLocation(Container, "r"), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "r", VersionId = "v1" });

        // Act
        await NewClient(repository.Object).CopyResourceAsync("r", Container, "copy", new ResourceCopyOptions());

        // Assert
        repository.Verify(r => r.CopyResourceAsync(new ResourceLocation(Container, "r"), "v1", It.IsAny<NewResourceVersion>(),
            It.Is<ResourceWriteCondition?>(c => c!.CreateOnly && c.Conditions == null), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task UploadAsync_ShouldPassTheConditionsWithoutCreateOnly_WhenGivenConditionsInReplaceMode()
    {
        // Arrange
        var repository = NewRepository();
        var conditions = new ResourceConditions { IfMatchVersionId = "v1" };

        // Act
        await NewClient(repository.Object).UploadAsync("r", new MemoryStream(), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace, Conditions = conditions });

        // Assert
        repository.Verify(r => r.AddResourceAsync(It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(),
            It.Is<ResourceWriteCondition?>(c => !c!.CreateOnly && c.Conditions == conditions), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldPassNoConditions_WhenGivenEmptyConditions()
    {
        // Arrange
        var repository = NewRepository();

        // Act
        await NewClient(repository.Object, versioning: true).UploadAsync("r", new MemoryStream(),
            new ResourceUploadOptions { Conditions = new ResourceConditions() });

        // Assert
        repository.Verify(r => r.AddResourceAsync(It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(),
            It.Is<ResourceWriteCondition?>(c => c!.Conditions == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldPassNoConditions_WhenGivenEmptyConditions()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.DeleteResourceAsync(new ResourceLocation(Container, "r"), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        bool deleted = await NewClient(repository.Object).DeleteResourceIfExistsAsync("r", null, new ResourceConditions(),
            CancellationToken.None);

        // Assert
        Assert.True(deleted);
    }
}
