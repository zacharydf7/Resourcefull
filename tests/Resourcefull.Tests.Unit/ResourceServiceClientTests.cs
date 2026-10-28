using Microsoft.Extensions.Options;
using Moq;

namespace Resourcefull.Tests.Unit;

public class ResourceServiceClientTests
{
    private static ResourceServiceClient NewService(IResourceRepository repository, IUriProvider? uriProvider = null)
        => new(repository, Options.Create(new ResourcefullOptions()), uriProvider);

    public static TheoryData<string?> InvalidContainerNames() =>
    [
        null,
        "",
        "   ",
        " leading",
        "trailing ",
        new string('a', ResourceNames.MaxContainerNameLength + 1)
    ];

    [Theory]
    [MemberData(nameof(InvalidContainerNames))]
    public async Task EveryContainerNameEntryPoint_ShouldThrowArgumentException_WhenGivenAnInvalidName(string? name)
    {
        // Arrange
        var repository = new Mock<IResourceRepository>(MockBehavior.Strict);
        var service = NewService(repository.Object);

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => service.GetResourceContainerClient(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CreateResourceContainerAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CreateResourceContainerIfNotExistsAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.GetResourceContainerInfoAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ContainerExistsAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.DeleteResourceContainerIfExistsAsync(name!));
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateResourceContainerAsync_ShouldCreateContainer_WhenGivenANameAtTheMaximumLength()
    {
        // Arrange
        var name = new string('a', ResourceNames.MaxContainerNameLength);
        var repository = new Mock<IResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.CreateResourceContainerAsync(name);

        // Assert
        repository.Verify(r => r.AddResourceContainerAsync(It.Is<ResourceContainerInfo>(c => c.Name == name), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetResourceContainerClient_ShouldNotTouchTheRepository_WhenClientIsCreated()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>(MockBehavior.Strict);
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = service.GetResourceContainerClient("myContainer");

        // Assert
        Assert.Equal("myContainer", client.ResourceContainerInfo.Name);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateResourceContainerAsync_ShouldCreateContainer_WhenItDoesNotExist()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("newContainer", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateResourceContainerAsync("newContainer");

        // Assert
        Assert.Equal("newContainer", client.ResourceContainerInfo.Name);
        repository.Verify(r => r.AddResourceContainerAsync(It.Is<ResourceContainerInfo>(c => c.Name == "newContainer"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateResourceContainerAsync_ShouldThrowConflict_WhenContainerAlreadyExists()
    {
        // Arrange & Act
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceContainerInfo { Name = "existing" });
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(() => service.CreateResourceContainerAsync("existing"));
        repository.Verify(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldNotCreate_WhenContainerExists()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceContainerInfo { Name = "existing" });
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateResourceContainerIfNotExistsAsync("existing");

        // Assert
        Assert.Equal("existing", client.ResourceContainerInfo.Name);
        repository.Verify(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldCreate_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("fresh", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        await service.CreateResourceContainerIfNotExistsAsync("fresh");

        // Assert
        repository.Verify(r => r.AddResourceContainerAsync(It.Is<ResourceContainerInfo>(c => c.Name == "fresh"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldCreateOnce_WhenCalledConcurrently()
    {
        // Arrange
        // Stateful, thread-safe repository: existence reflects what has actually been added.
        var gate = new object();
        var added = new List<ResourceContainerInfo>();
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => { lock (gate) { return added.FirstOrDefault(c => c.Name == name); } });
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .Callback<ResourceContainerInfo, CancellationToken>((c, _) => { lock (gate) { added.Add(c); } })
            .Returns(Task.CompletedTask);
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient[] clients = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ =>
                Task.Run(() => service.CreateResourceContainerIfNotExistsAsync("shared"))));

        // Assert
        Assert.All(clients, c => Assert.Equal("shared", c.ResourceContainerInfo.Name));
        repository.Verify(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldReturnClient_WhenAddLosesCrossProcessRace()
    {
        // Arrange
        // The container does not exist on the pre-create checks, the add fails (another instance won
        // the race), and a re-check then shows it exists — the method should swallow and return.
        var repository = new Mock<IResourceRepository>();
        repository.SetupSequence(r => r.GetResourceContainerInfoAsync("racy", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceContainerInfo?)null)                // fast-path check
            .ReturnsAsync((ResourceContainerInfo?)null)                // re-check under the lock
            .ReturnsAsync(new ResourceContainerInfo { Name = "racy" }); // re-check after the failed add
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateResourceContainerIfNotExistsAsync("racy");

        // Assert
        Assert.Equal("racy", client.ResourceContainerInfo.Name);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldRethrow_WhenAddFailsAndContainerStillMissing()
    {
        // Arrange & Act
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("boom", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceContainerInfo?)null);
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateResourceContainerIfNotExistsAsync("boom"));
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldReportCancellation_WhenTheAddIsCancelled()
    {
        // Arrange & Act
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("cancelled", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceContainerInfo?)null);
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateResourceContainerIfNotExistsAsync("cancelled"));
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldReturnNull_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        ResourceContainerInfo? info = await service.GetResourceContainerInfoAsync("missing");

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldReturnFalse_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        bool deleted = await service.DeleteResourceContainerIfExistsAsync("missing");

        // Assert
        Assert.False(deleted);
        repository.Verify(r => r.DeleteResourceContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldDelete_WhenContainerExists()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceContainerInfo { Name = "existing" });
        repository.Setup(r => r.DeleteResourceContainerAsync("existing", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = NewService(repository.Object);

        // Act
        bool deleted = await service.DeleteResourceContainerIfExistsAsync("existing");

        // Assert
        Assert.True(deleted);
    }

    [Fact]
    public async Task CreateResourceContainerAsync_ShouldPassCancellationTokenNone_WhenCalledWithoutAToken()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("name", It.IsAny<CancellationToken>())).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        await service.CreateResourceContainerAsync("name");

        // Assert
        repository.Verify(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task CreateResourceContainerAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("name", cts.Token)).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        await service.CreateResourceContainerAsync("name", null, cts.Token);

        // Assert
        repository.Verify(r => r.GetResourceContainerInfoAsync("name", cts.Token), Times.Once);
        repository.Verify(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("name", cts.Token)).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        await service.CreateResourceContainerIfNotExistsAsync("name", null, cts.Token);

        // Assert
        repository.Verify(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.GetResourceContainerInfoAsync("name", cts.Token);

        // Assert
        repository.Verify(r => r.GetResourceContainerInfoAsync("name", cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetResourceContainersAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetAllResourceContainerInfoAsync(cts.Token)).ReturnsAsync([]);
        var service = NewService(repository.Object);

        // Act
        await service.GetResourceContainersAsync(cts.Token);

        // Assert
        repository.Verify(r => r.GetAllResourceContainerInfoAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task ContainerExistsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("name", cts.Token)).ReturnsAsync((ResourceContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        await service.ContainerExistsAsync("name", cts.Token);

        // Assert
        repository.Verify(r => r.GetResourceContainerInfoAsync("name", cts.Token), Times.Once);
    }

    [Fact]
    public async Task DeleteResourceContainerIfExistsAsync_ShouldPassTheToken_WhenContainerExists()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("name", cts.Token)).ReturnsAsync(new ResourceContainerInfo { Name = "name" });
        var service = NewService(repository.Object);

        // Act
        await service.DeleteResourceContainerIfExistsAsync("name", cts.Token);

        // Assert
        repository.Verify(r => r.DeleteResourceContainerAsync("name", cts.Token), Times.Once);
    }
}
