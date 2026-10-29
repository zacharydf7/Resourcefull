using Moq;

namespace Resourcefull.Tests.Unit;

public class ResourceConditionsTests
{
    private const string Container = "containerName";

    private static readonly WrittenContent Written = new(1, "sha256:written", DateTimeOffset.UnixEpoch);

    private static ResourceContainerClient NewClient(IResourceRepository repository, bool versioning = false) =>
        new(repository, null, new ResourcefullOptions { IsVersioningEnabled = versioning }, new ResourceContainerInfo { Name = Container }, null);

    private static Mock<IResourceRepository> NewRepository()
    {
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(Written);
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(),
            It.IsAny<ResourceWriteCondition>(), It.IsAny<CancellationToken>())).ReturnsAsync(Written);
        return repository;
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowArgumentException_WhenCreateOnlyAndConditionsAreBothSet()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>(MockBehavior.Strict);
        var options = new ResourceUploadOptions { CreateOnly = true, Conditions = new ResourceConditions { IfMatchVersionId = "v1" } };

        // Act
        Task act() => NewClient(repository.Object).UploadAsync("r", new MemoryStream(), options);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(act);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadAsync_ShouldPassTheConditionsAsAnOverwrite_WhenGivenConditionsWithoutVersioningOrOverwrite()
    {
        // Arrange
        var repository = NewRepository();
        var conditions = new ResourceConditions { IfMatchVersionId = "v1" };

        // Act
        await NewClient(repository.Object).UploadAsync("r", new MemoryStream(), new ResourceUploadOptions { Conditions = conditions });

        // Assert
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(),
            It.Is<ResourceWriteCondition>(c => !c.CreateOnly && c.Conditions == conditions), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldUseTheUnconditionalOverload_WhenGivenEmptyConditions()
    {
        // Arrange: a provider written for 3.0 only implements the unconditional overload.
        var repository = NewRepository();

        // Act
        await NewClient(repository.Object, versioning: true).UploadAsync("r", new MemoryStream(),
            new ResourceUploadOptions { Conditions = new ResourceConditions() });

        // Assert
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), false,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(),
            It.IsAny<ResourceWriteCondition>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldUseTheUnconditionalOverload_WhenGivenNoConditions()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.DeleteResourceAsync(Container, "r", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        bool deleted = await NewClient(repository.Object).DeleteResourceIfExistsAsync("r", null, null, CancellationToken.None);

        // Assert
        Assert.True(deleted);
        repository.Verify(r => r.DeleteResourceAsync(Container, "r", null, It.IsAny<ResourceConditions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowNotSupported_WhenAProviderWrittenFor30IsGivenConditions()
    {
        // Arrange
        IResourceRepository repository = new Mock<IResourceRepository> { CallBase = true }.Object;
        var condition = new ResourceWriteCondition { Conditions = new ResourceConditions { IfMatchVersionId = "v1" } };

        // Act
        Task act() => repository.AddResourceAsync(Container, new ResourceInfo { ContainerName = Container, Name = "r" }, new MemoryStream(), condition);

        // Assert
        await Assert.ThrowsAsync<NotSupportedException>(act);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldCallTheUnconditionalOverload_WhenAProviderWrittenFor30IsGivenOnlyCreateOnly()
    {
        // Arrange
        var mock = new Mock<IResourceRepository> { CallBase = true };
        mock.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), true,
            It.IsAny<CancellationToken>())).ReturnsAsync(Written);

        // Act
        WrittenContent written = await mock.Object.AddResourceAsync(Container, new ResourceInfo { ContainerName = Container, Name = "r" }, new MemoryStream(),
            new ResourceWriteCondition { CreateOnly = true });

        // Assert
        Assert.Same(Written, written);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldThrowNotSupported_WhenAProviderWrittenFor30IsGivenConditions()
    {
        // Arrange
        IResourceRepository repository = new Mock<IResourceRepository> { CallBase = true }.Object;

        // Act
        Task act() => repository.DeleteResourceAsync(Container, "r", null, new ResourceConditions { IfMatchContentHash = "sha256:00" });

        // Assert
        await Assert.ThrowsAsync<NotSupportedException>(act);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldThrowNotSupported_WhenAClientWrittenFor30IsGivenConditions()
    {
        // Arrange
        IResourceContainerClient client = new Mock<IResourceContainerClient> { CallBase = true }.Object;

        // Act
        Task act() => client.DeleteResourceIfExistsAsync("r", null, new ResourceConditions { IfMatchVersionId = "v1" }, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<NotSupportedException>(act);
    }
}
