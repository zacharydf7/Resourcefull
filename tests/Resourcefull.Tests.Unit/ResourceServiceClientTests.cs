using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Resourcefull.InMemory;
using Resourcefull.Providers;

namespace Resourcefull.Tests.Unit;

public class ResourceServiceClientTests
{
    private static ResourceServiceClient NewService(ResourceRepository repository, IUriProvider? uriProvider = null)
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
        var repository = new Mock<ResourceRepository>(MockBehavior.Strict);
        var service = NewService(repository.Object);

        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => service.GetContainerClient(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CreateContainerAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CreateContainerIfNotExistsAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.GetContainerInfoAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ContainerExistsAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.DeleteContainerIfExistsAsync(name!));
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateContainerAsync_ShouldCreateContainer_WhenGivenANameAtTheMaximumLength()
    {
        // Arrange
        var name = new string('a', ResourceNames.MaxContainerNameLength);
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.CreateContainerAsync(name);

        // Assert
        repository.Verify(r => r.AddContainerAsync(name, It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void GetContainerClient_ShouldNotTouchTheRepository_WhenClientIsCreated()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>(MockBehavior.Strict);
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = service.GetContainerClient("myContainer");

        // Assert
        Assert.Equal("myContainer", client.Info.Name);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetContainerClient_ShouldCarryTheUriProvidersUri_WhenAUriProviderIsConfigured()
    {
        // Arrange
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(new ResourcefullContext("myContainer", null, null))).Returns("https://resources.test/myContainer");
        var service = NewService(new Mock<ResourceRepository>(MockBehavior.Strict).Object, uriProvider.Object);

        // Act
        IResourceContainerClient client = service.GetContainerClient("myContainer");

        // Assert
        Assert.Equal("https://resources.test/myContainer", client.Info.Uri);
    }

    private static IUriProvider NewBaseUrlUriProvider()
    {
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(It.IsAny<ResourcefullContext>()))
            .Returns((ResourcefullContext context) => $"https://new.test/{context.ContainerName}");
        return uriProvider.Object;
    }

    private static Mock<ResourceRepository> NewRepositoryWithStoredUris()
    {
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("stored", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerInfo { Name = "stored", Uri = "https://old.test/stored" });
        repository.Setup(r => r.GetAllContainerInfoAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ContainerInfo { Name = "first", Uri = "https://old.test/first" },
            new ContainerInfo { Name = "second", Uri = "https://old.test/second" }
        ]);
        return repository;
    }

    [Fact]
    public async Task GetContainerInfoAsync_ShouldReturnTheUriProvidersUri_WhenTheStoredUriIsOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        ContainerInfo? containerInfo = await service.GetContainerInfoAsync("stored");

        // Assert
        Assert.Equal("https://new.test/stored", containerInfo!.Uri);
    }

    [Fact]
    public async Task GetContainerInfoAsync_ShouldReturnNoUri_WhenNoUriProviderIsConfigured()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object);

        // Act
        ContainerInfo? containerInfo = await service.GetContainerInfoAsync("stored");

        // Assert
        Assert.Null(containerInfo!.Uri);
    }

    [Fact]
    public async Task GetContainersAsync_ShouldReturnTheUriProvidersUris_WhenTheStoredUrisAreOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        IReadOnlyList<ContainerInfo> containers = await service.GetContainersAsync();

        // Assert
        Assert.Equal(["https://new.test/first", "https://new.test/second"], containers.Select(c => c.Uri));
    }

    [Fact]
    public async Task ContainerExistsAsync_ShouldNotAskTheUriProvider_WhenCalled()
    {
        // Arrange: a provider that only works inside a request, called from outside one.
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(It.IsAny<ResourcefullContext>())).Throws<InvalidOperationException>();
        var service = NewService(NewRepositoryWithStoredUris().Object, uriProvider.Object);

        // Act
        bool exists = await service.ContainerExistsAsync("stored");

        // Assert
        Assert.True(exists);
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldReturnTheUriProvidersUri_WhenTheExistingContainersStoredUriIsOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        IResourceContainerClient client = await service.CreateContainerIfNotExistsAsync("stored");

        // Assert
        Assert.Equal("https://new.test/stored", client.Info.Uri);
    }

    [Fact]
    public async Task CreateContainerAsync_ShouldCreateContainer_WhenItDoesNotExist()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateContainerAsync("newContainer");

        // Assert
        Assert.Equal("newContainer", client.Info.Name);
        repository.Verify(r => r.AddContainerAsync("newContainer", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateContainerAsync_ShouldThrowConflict_WhenContainerAlreadyExists()
    {
        // Arrange & Act
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(() => service.CreateContainerAsync("existing"));
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldNotCreate_WhenContainerExists()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerInfo { Name = "existing" });
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateContainerIfNotExistsAsync("existing");

        // Assert
        Assert.Equal("existing", client.Info.Name);
        repository.Verify(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldReturnAClientWithTheStoredInfo_WhenAConcurrentCreateWinsTheAdd()
    {
        // Arrange: absent when checked, then created by someone else, with their metadata, before this add.
        var repository = new Mock<ResourceRepository>();
        repository.SetupSequence(r => r.GetContainerInfoAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContainerInfo?)null)
            .ReturnsAsync(new ContainerInfo { Name = "existing", Uri = "https://old.test/existing", Metadata = new Dictionary<string, string> { ["owner"] = "them" } });
        repository.Setup(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object, NewBaseUrlUriProvider());

        // Act
        IResourceContainerClient client = await service.CreateContainerIfNotExistsAsync("existing", new Dictionary<string, string> { ["owner"] = "us" });

        // Assert
        Assert.Equal("them", client.Info.Metadata["owner"]);
        Assert.Equal("https://new.test/existing", client.Info.Uri);
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldReturnAClientForTheName_WhenTheConcurrentlyCreatedContainerIsGoneAgain()
    {
        // Arrange: created by someone else before this add, and deleted again before it can be read.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateContainerIfNotExistsAsync("existing", new Dictionary<string, string> { ["owner"] = "us" });

        // Assert
        Assert.Equal("existing", client.Info.Name);
        Assert.Empty(client.Info.Metadata);
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldCreate_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.CreateContainerIfNotExistsAsync("fresh");

        // Assert
        repository.Verify(r => r.AddContainerAsync("fresh", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldRethrow_WhenTheAddFailsForAnotherReason()
    {
        // Arrange & Act
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateContainerIfNotExistsAsync("boom"));
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldReportCancellation_WhenTheAddIsCanceled()
    {
        // Arrange & Act
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateContainerIfNotExistsAsync("canceled"));
    }

    [Fact]
    public async Task GetContainerInfoAsync_ShouldReturnNull_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        ContainerInfo? info = await service.GetContainerInfoAsync("missing");

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldReturnFalse_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        bool deleted = await service.DeleteContainerIfExistsAsync("missing");

        // Assert
        Assert.False(deleted);
        repository.Verify(r => r.DeleteContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldDelete_WhenContainerExists()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerInfo { Name = "existing" });
        repository.Setup(r => r.DeleteContainerAsync("existing", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = NewService(repository.Object);

        // Act
        bool deleted = await service.DeleteContainerIfExistsAsync("existing");

        // Assert
        Assert.True(deleted);
    }

    [Fact]
    public async Task CreateContainerAsync_ShouldPassCancellationTokenNone_WhenCalledWithoutAToken()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.CreateContainerAsync("name");

        // Assert
        repository.Verify(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task CreateContainerAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.CreateContainerAsync("name", null, cts.Token);

        // Assert
        repository.Verify(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.CreateContainerIfNotExistsAsync("name", null, cts.Token);

        // Assert
        repository.Verify(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetContainerInfoAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.GetContainerInfoAsync("name", cts.Token);

        // Assert
        repository.Verify(r => r.GetContainerInfoAsync("name", cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetContainersAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetAllContainerInfoAsync(cts.Token)).ReturnsAsync([]);
        var service = NewService(repository.Object);

        // Act
        await service.GetContainersAsync(cts.Token);

        // Assert
        repository.Verify(r => r.GetAllContainerInfoAsync(cts.Token), Times.Once);
    }

    [Fact]
    public async Task ContainerExistsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("name", cts.Token)).ReturnsAsync((ContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        await service.ContainerExistsAsync("name", cts.Token);

        // Assert
        repository.Verify(r => r.GetContainerInfoAsync("name", cts.Token), Times.Once);
    }

    [Fact]
    public async Task DeleteContainerIfExistsAsync_ShouldPassTheToken_WhenContainerExists()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("name", cts.Token)).ReturnsAsync(new ContainerInfo { Name = "name" });
        var service = NewService(repository.Object);

        // Act
        await service.DeleteContainerIfExistsAsync("name", cts.Token);

        // Assert
        repository.Verify(r => r.DeleteContainerAsync("name", cts.Token), Times.Once);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        var expired = new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "v1", ExpiresAt = DateTimeOffset.UnixEpoch };
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>())).ReturnsAsync([expired]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<(ResourceLocation, string)>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([(new ResourceLocation("c", "n"), "v1")]);
        var service = NewService(repository.Object);

        // Act
        await service.DeleteExpiredAsync(cts.Token);

        // Assert: the check for any, the page, and its batch delete.
        repository.Verify(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null, cts.Token), Times.Exactly(2));
        repository.Verify(r => r.DeleteExpiredVersionsAsync(
            It.Is<IReadOnlyList<(ResourceLocation, string)>>(v => IsOnly(v, "c", "n", "v1")),
            It.IsAny<DateTimeOffset>(), cts.Token), Times.Once);
    }

    private static bool IsOnly(IReadOnlyList<(ResourceLocation, string)> versions, string container, string name,
        string versionId) =>
        versions.Count == 1 && versions[0] == (new ResourceLocation(container, name), versionId);

    /// <summary>
    /// Without versioning, an expired current version with the version it replaced waiting behind it, and a provider
    /// whose batch deletes that version and then fails with <paramref name="failure"/>.
    /// </summary>
    private static (Mock<ResourceRepository> Repository, ResourceServiceClient Service) NewSweepWhoseBatchFailsPartway(
        Func<Exception> failure)
    {
        var repository = new Mock<ResourceRepository>();
        var current = new ResourceInfo
        {
            ContainerName = "c", Name = "n", VersionId = "current", IsLatest = true, ExpiresAt = DateTimeOffset.UnixEpoch
        };
        var replaced = new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "replaced" };
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null,
            It.IsAny<CancellationToken>())).ReturnsAsync([current]);
        repository.Setup(r => r.GetResourceVersionsAsync(new ResourceLocation("c", "n"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([current, replaced]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<(ResourceLocation, string)>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromException<IReadOnlyCollection<(ResourceLocation, string)>>(failure()));
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceLocation("c", "n"), "current", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceInfo?)null);
        var service = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()),
            expiry: new ExpirySettings(TimeSpan.FromMinutes(1)));
        return (repository, service);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteWhatWaitedBehindAVersion_WhenAFailedBatchHadAlreadyDeletedIt()
    {
        // Arrange
        var (repository, service) = NewSweepWhoseBatchFailsPartway(() => new InvalidOperationException("The batch failed."));

        // Act
        int deleted = await service.DeleteExpiredAsync();

        // Assert: counted, and the replaced version didn't become current again.
        Assert.Equal(1, deleted);
        repository.Verify(r => r.DeleteResourceAsync(new ResourceLocation("c", "n"), "replaced", It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteWhatWaitedBehindAVersion_WhenCanceledWhileItsBatchRan()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var (repository, service) = NewSweepWhoseBatchFailsPartway(() =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });

        // Act
        Task act() => service.DeleteExpiredAsync(cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        repository.Verify(r => r.DeleteResourceAsync(new ResourceLocation("c", "n"), "replaced", It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldCountOnlyTheVersionsTheBatchDeleted_WhenTwoContainersShareAVersionId()
    {
        // Arrange: an import can give versions in two containers the same ID; only one of those two is still expired.
        var repository = new Mock<ResourceRepository>();
        ResourceInfo Expired(string container, string name, string id) =>
            new() { ContainerName = container, Name = name, VersionId = id, ExpiresAt = DateTimeOffset.UnixEpoch };
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null,
            It.IsAny<CancellationToken>())).ReturnsAsync([Expired("c", "n", "v1"), Expired("d", "n", "v1"), Expired("c", "o", "v2")]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.Is<IReadOnlyList<(ResourceLocation, string)>>(v => v.Count == 3),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(new ResourceLocation("c", "n"), "v1"), (new ResourceLocation("c", "o"), "v2")]);

        // Act
        int deleted = await NewService(repository.Object).DeleteExpiredAsync();

        // Assert
        Assert.Equal(2, deleted);
        repository.Verify(r => r.DeleteExpiredVersionAsync(It.IsAny<ResourceLocation>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldSayWhichResourceFailed_WhenTheProviderDoesNot()
    {
        // Arrange: a provider written before exceptions said what they're about.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.DeleteResourceAsync(new ResourceLocation("c", "n"), null, It.IsAny<ResourceConditions>(),
            It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourcePreconditionFailedException("Not met.", null, null));
        IResourceContainerClient container = NewService(repository.Object).GetContainerClient("c");

        // Act
        Task act() => container.DeleteResourceIfExistsAsync("n", null,
            new ResourceConditions { IfMatchVersionId = "v1" }, CancellationToken.None);

        // Assert
        var exception = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Equal(("c", "n"), (exception.ContainerName, exception.ResourceName));
    }

    [Fact]
    public async Task CopyResourceAsync_ShouldNameTheDestination_WhenTheProviderDoesNot()
    {
        // Arrange: a provider written before exceptions said what they're about, whose destination name exists.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceLocation("c", "a"), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v1" });
        repository.Setup(r => r.CopyResourceAsync(It.IsAny<ResourceLocation>(), "v1", It.IsAny<NewResourceVersion>(),
            It.IsAny<ResourceWriteCondition?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("Exists."));
        IResourceContainerClient container = NewService(repository.Object).GetContainerClient("c");

        // Act
        Task act() => container.CopyResourceAsync("a", "d", "b");

        // Assert
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal(("d", "b"), (exception.ContainerName, exception.ResourceName));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldNameOnlyTheDestinationContainer_WhenTheProviderDoesNotAndItIsMissing()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.MoveResourceAsync(It.IsAny<ResourceLocation>(), It.IsAny<ResourceLocation>(),
            It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceContainerNotFoundException("Missing."));
        IResourceContainerClient container = NewService(repository.Object).GetContainerClient("c");

        // Act
        Task act() => container.MoveResourceAsync("a", "d", "b");

        // Assert
        var exception = await Assert.ThrowsAsync<ResourceContainerNotFoundException>(act);
        Assert.Equal(("d", null), (exception.ContainerName, exception.ResourceName));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldDeleteEachVersionOnItsOwn_WhenTheBatchFails()
    {
        // Arrange: two expired versions, and a provider whose batch delete fails but whose single deletes work.
        var repository = new Mock<ResourceRepository>();
        ResourceInfo Expired(string id) =>
            new() { ContainerName = "c", Name = id, VersionId = id, ExpiresAt = DateTimeOffset.UnixEpoch };
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null,
            It.IsAny<CancellationToken>())).ReturnsAsync([Expired("a"), Expired("b")]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<(ResourceLocation, string)>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The batch failed."));
        repository.Setup(r => r.DeleteExpiredVersionAsync(It.IsAny<ResourceLocation>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        int deleted = await NewService(repository.Object).DeleteExpiredAsync();

        // Assert
        Assert.Equal(2, deleted);
        foreach (string id in new[] { "a", "b" })
        {
            repository.Verify(r => r.DeleteExpiredVersionAsync(new ResourceLocation("c", id), id,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task ExportContainerAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        var version = new ResourceInfo { ContainerName = "name", Name = "a", VersionId = "v1" };
        repository.Setup(r => r.GetContainerInfoAsync("name", It.IsAny<CancellationToken>())).ReturnsAsync(new ContainerInfo { Name = "name" });
        repository.Setup(r => r.GetVersionPageAsync("name", null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([version]);
        repository.Setup(r => r.GetVersionPageAsync("name", "a", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetResourceStreamAsync(new ResourceLocation("name", "a"), "v1", It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream([1]));
        var service = NewService(repository.Object);

        // Act
        await service.ExportContainerAsync("name", new MemoryStream(), cts.Token);

        // Assert: the container, its page of names' versions, and each version's content.
        repository.Verify(r => r.GetContainerInfoAsync("name", cts.Token), Times.Once);
        repository.Verify(r => r.GetVersionPageAsync("name", null, It.IsAny<int>(), cts.Token), Times.Once);
        repository.Verify(r => r.GetResourceStreamAsync(new ResourceLocation("name", "a"), "v1", cts.Token), Times.Once);
    }

    [Fact]
    public async Task ExportContainerAsync_ShouldReadUntilAnEmptyPage_WhenAPageHasFewerNamesThanItAskedFor()
    {
        // Arrange: a provider whose first page comes back short, as one can when a name is deleted while it's read,
        // and whose next page still has a name.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("c", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerInfo { Name = "c" });
        ResourceInfo Version(string name) => new() { ContainerName = "c", Name = name, VersionId = name };
        repository.Setup(r => r.GetVersionPageAsync("c", null, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Version("a")]);
        repository.Setup(r => r.GetVersionPageAsync("c", "a", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Version("b")]);
        repository.Setup(r => r.GetVersionPageAsync("c", "b", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        repository.Setup(r => r.GetResourceStreamAsync(It.Is<ResourceLocation>(l => l.ContainerName == "c"), It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream([1]));

        // Act
        ResourceExportResult exported =
            await NewService(repository.Object).ExportContainerAsync("c", new MemoryStream());

        // Assert
        Assert.Equal(2, exported.ResourceCount);
    }

    /// <summary>An export of one file, "a", whose container has the metadata team=billing.</summary>
    private static async Task<MemoryStream> OneFileExportAsync()
    {
        using ServiceProvider services = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient source = services.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await source.CreateContainerAsync("c", new Dictionary<string, string> { ["team"] = "billing" });
        await container.UploadAsync("a", BinaryData.FromString("hello"));
        var export = new MemoryStream();
        await source.ExportContainerAsync("c", export);
        export.Position = 0;
        return export;
    }

    private static readonly WrittenContent Hello =
        new(5, "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", DateTimeOffset.UtcNow);

    [Fact]
    public async Task ImportContainerAsync_ShouldMarkTheContainerUntilEveryVersionIsStored_WhenImporting()
    {
        // Arrange: a provider that records what the import does, in order.
        var steps = new List<string>();
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.SetContainerMetadataAsync("d", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback((string _, IReadOnlyDictionary<string, string> metadata, CancellationToken _) =>
                steps.Add("metadata: " + string.Join(", ", metadata.OrderBy(e => e.Key).Select(e => $"{e.Key}={e.Value}"))))
            .ReturnsAsync(true);
        repository.Setup(r => r.ImportVersionAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback((NewResourceVersion version, Stream _, CancellationToken _) => steps.Add("version: " + version.Location.ResourceName))
            .ReturnsAsync(Hello);
        var client = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));

        // Act
        await client.ImportContainerAsync("d", await OneFileExportAsync());

        // Assert
        Assert.Equal(["metadata: resourcefull.import=in-progress, team=billing", "version: a", "metadata: team=billing"], steps);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldNotKeepTheMark_WhenTheExportedContainerWasMarked()
    {
        // Arrange: an export of a container whose own import hadn't finished.
        using ServiceProvider services = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient service = services.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient marked = await service.CreateContainerAsync("marked",
            new Dictionary<string, string> { ["team"] = "billing", ["resourcefull.import"] = "in-progress" });
        await marked.UploadAsync("a", BinaryData.FromString("hello"));
        var export = new MemoryStream();
        await service.ExportContainerAsync("marked", export);
        export.Position = 0;

        // Act
        await service.ImportContainerAsync("restored", export);

        // Assert
        Assert.Equal(new Dictionary<string, string> { ["team"] = "billing" }, (await service.GetContainerInfoAsync("restored"))!.Metadata);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldImportUnmarked_WhenTheProviderCannotChangeContainerMetadata()
    {
        // Arrange: SetContainerMetadataAsync keeps its default, which throws NotSupportedException.
        var repository = new Mock<ResourceRepository> { CallBase = true };
        repository.Setup(r => r.ImportVersionAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Hello);
        var client = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));

        // Act
        ResourceImportResult imported = await client.ImportContainerAsync("d", await OneFileExportAsync());

        // Assert
        Assert.Equal(1, imported.VersionCount);
        repository.Verify(r => r.DeleteContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldThrowBothFailures_WhenTheImportFailsAndSoDoesDeletingWhatItCreated()
    {
        // Arrange: a provider that can't store the version, and then can't delete the container either.
        var repository = new Mock<ResourceRepository> { CallBase = true };
        var storing = new IOException("The disk is full.");
        var deleting = new InvalidOperationException("The database is unreachable.");
        repository.Setup(r => r.ImportVersionAsync( It.IsAny<NewResourceVersion>(),
            It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ThrowsAsync(storing);
        repository.Setup(r => r.DeleteContainerAsync("d", It.IsAny<CancellationToken>())).ThrowsAsync(deleting);
        var client = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));
        Stream export = await OneFileExportAsync();

        // Act
        Task act() => client.ImportContainerAsync("d", export);

        // Assert: both, so the caller knows to delete the container before importing again.
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(act);
        Assert.Equal([storing, deleting], failure.InnerExceptions);
        Assert.Contains("delete the container", failure.Message);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldGiveTheProviderContentItCanReadAgain_WhenImporting()
    {
        // Arrange: an export of one file, and a provider that reads each version twice, as one that retries does.
        using ServiceProvider services = new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceServiceClient source = services.GetRequiredService<IResourceServiceClient>();
        await (await source.CreateContainerAsync("c")).UploadAsync("a", BinaryData.FromString("hello"));
        var export = new MemoryStream();
        await source.ExportContainerAsync("c", export);
        export.Position = 0;
        var reads = new List<string>();
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.ImportVersionAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns(async (NewResourceVersion _, Stream content, CancellationToken _) =>
            {
                reads.Add(await new StreamReader(content, leaveOpen: true).ReadToEndAsync());
                content.Position = 0;
                reads.Add(await new StreamReader(content, leaveOpen: true).ReadToEndAsync());
                return new WrittenContent(5, "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", DateTimeOffset.UtcNow);
            });
        var client = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()));

        // Act
        await client.ImportContainerAsync("d", export);

        // Assert
        Assert.Equal(["hello", "hello"], reads);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange: the export of a container holding one version of "a".
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        string hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData([1]));
        repository.Setup(r => r.ImportVersionAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WrittenContent(1, hash, DateTimeOffset.UnixEpoch));
        var export = new MemoryStream();
        using (var zip = new ZipArchive(export, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream content = zip.CreateEntry("content/one").Open()) content.WriteByte(1);
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write($$"""
                { "format": 1, "metadata": {}, "resources": [ { "name": "a", "versions": [ {
                  "versionId": "v1", "lastModified": "2026-01-01T00:00:00+00:00", "contentType": null, "contentLength": 1,
                  "contentHash": "{{hash}}", "metadata": {}, "copiedFrom": null, "expiresAt": null, "content": "content/one" } ] } ] }
                """);
        }
        export.Position = 0;
        var service = NewService(repository.Object);

        // Act
        await service.ImportContainerAsync("name", export, cts.Token);

        // Assert: the container, and each version.
        repository.Verify(r => r.AddContainerAsync("name", It.IsAny<IReadOnlyDictionary<string, string>>(), cts.Token), Times.Once);
        repository.Verify(r => r.ImportVersionAsync(It.Is<NewResourceVersion>(v => v.VersionId == "v1"), It.IsAny<Stream>(), cts.Token), Times.Once);
    }
}
