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
    private static ResourceServiceClient NewService(ResourceRepository repository, IResourceUriProvider? uriProvider = null)
        => new(repository, Options.Create(new ResourcefullOptions()), uriProvider);

    public static TheoryData<string?> InvalidContainerNames() =>
    [
        (string?)null,
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
        Assert.Equal("myContainer", client.Name);
        repository.VerifyNoOtherCalls();
    }

    private static IResourceUriProvider NewBaseUrlUriProvider()
    {
        var uriProvider = new Mock<IResourceUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(It.IsAny<ResourceUriContext>()))
            .Returns((ResourceUriContext context) => $"https://new.test/{context.ContainerName}");
        return uriProvider.Object;
    }

    private static Mock<ResourceRepository> NewRepositoryWithStoredUris()
    {
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("stored", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContainerInfo { Name = "stored", Uri = "https://old.test/stored" });
        repository.Setup(r => r.GetContainerPageAsync(null, null, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ContainerInfo { Name = "first", Uri = "https://old.test/first" },
            new ContainerInfo { Name = "second", Uri = "https://old.test/second" }
        ]);
        return repository;
    }

    [Fact]
    public async Task GetInfoAsync_ShouldReturnTheUriProvidersUri_WhenTheStoredUriIsOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        ContainerInfo? containerInfo = await service.GetContainerClient("stored").GetInfoAsync();

        // Assert
        Assert.Equal("https://new.test/stored", containerInfo!.Uri);
    }

    [Fact]
    public async Task GetInfoAsync_ShouldReturnNoUri_WhenNoUriProviderIsConfigured()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object);

        // Act
        ContainerInfo? containerInfo = await service.GetContainerClient("stored").GetInfoAsync();

        // Assert
        Assert.Null(containerInfo!.Uri);
    }

    [Fact]
    public async Task GetContainerPageAsync_ShouldReturnTheUriProvidersUris_WhenTheStoredUrisAreOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        ContainerPage page = await service.GetContainerPageAsync();

        // Assert
        Assert.Equal(["https://new.test/first", "https://new.test/second"], page.Containers.Select(c => c.Uri));
    }

    [Fact]
    public async Task ContainerExistsAsync_ShouldNotAskTheUriProvider_WhenCalled()
    {
        // Arrange: a provider that only works inside a request, called from outside one.
        var uriProvider = new Mock<IResourceUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(It.IsAny<ResourceUriContext>())).Throws<InvalidOperationException>();
        var service = NewService(NewRepositoryWithStoredUris().Object, uriProvider.Object);

        // Act
        bool exists = await service.ContainerExistsAsync("stored");

        // Assert
        Assert.True(exists);
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
        Assert.Equal("newContainer", client.Name);
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
        Assert.Equal("existing", client.Name);
        repository.Verify(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateContainerIfNotExistsAsync_ShouldReturnAClientForTheName_WhenAConcurrentCreateWinsTheAdd()
    {
        // Arrange: absent when checked, then created by someone else before this add.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.AddContainerAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateContainerIfNotExistsAsync("existing", new Dictionary<string, string> { ["owner"] = "us" });

        // Assert
        Assert.Equal("existing", client.Name);
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
    public async Task GetInfoAsync_ShouldReturnNull_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerInfoAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((ContainerInfo?)null);
        var service = NewService(repository.Object);

        // Act
        ContainerInfo? info = await service.GetContainerClient("missing").GetInfoAsync();

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
    public async Task GetInfoAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.GetContainerClient("name").GetInfoAsync(cts.Token);

        // Assert
        repository.Verify(r => r.GetContainerInfoAsync("name", cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetContainerPageAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.GetContainerPageAsync(null, null, It.IsAny<int>(), cts.Token)).ReturnsAsync([]);
        var service = NewService(repository.Object);

        // Act
        await service.GetContainerPageAsync(cancellationToken: cts.Token);

        // Assert
        repository.Verify(r => r.GetContainerPageAsync(null, null, It.IsAny<int>(), cts.Token), Times.Once);
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
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<ResourceVersionAddress>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync([new ResourceVersionAddress(new ResourceAddress("c", "n"), "v1")]);
        var service = NewService(repository.Object);

        // Act
        await service.DeleteExpiredAsync(cts.Token);

        // Assert: the check for any, the page, and its batch delete.
        repository.Verify(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null, cts.Token), Times.Exactly(2));
        repository.Verify(r => r.DeleteExpiredVersionsAsync(
            It.Is<IReadOnlyList<ResourceVersionAddress>>(v => IsOnly(v, "c", "n", "v1")),
            It.IsAny<DateTimeOffset>(), cts.Token), Times.Once);
    }

    private static bool IsOnly(IReadOnlyList<ResourceVersionAddress> versions, string container, string name,
        string versionId) =>
        versions.Count == 1 && versions[0] == new ResourceVersionAddress(new ResourceAddress(container, name), versionId);

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
        repository.Setup(r => r.GetResourceVersionsAsync(new ResourceAddress("c", "n"), It.IsAny<CancellationToken>()))
            .ReturnsAsync([current, replaced]);
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<ResourceVersionAddress>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromException<IReadOnlyCollection<ResourceVersionAddress>>(failure()));
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceAddress("c", "n"), "current", It.IsAny<CancellationToken>()))
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
        long deleted = await service.DeleteExpiredAsync();

        // Assert: counted, and the replaced version didn't become current again.
        Assert.Equal(1, deleted);
        repository.Verify(r => r.DeleteResourceAsync(new ResourceAddress("c", "n"), "replaced", It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()), Times.Once);
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
        repository.Verify(r => r.DeleteResourceAsync(new ResourceAddress("c", "n"), "replaced", It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()), Times.Once);
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
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.Is<IReadOnlyList<ResourceVersionAddress>>(v => v.Count == 3),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceVersionAddress(new ResourceAddress("c", "n"), "v1"), new ResourceVersionAddress(new ResourceAddress("c", "o"), "v2")]);

        // Act
        long deleted = await NewService(repository.Object).DeleteExpiredAsync();

        // Assert
        Assert.Equal(2, deleted);
        repository.Verify(r => r.DeleteExpiredVersionAsync(It.IsAny<ResourceAddress>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldSayWhichResourceFailed_WhenTheProviderDoesNot()
    {
        // Arrange: a provider written before exceptions said what they're about.
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.DeleteResourceAsync(new ResourceAddress("c", "n"), null, It.IsAny<ResourceConditions>(),
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
        repository.Setup(r => r.GetResourceInfoAsync(new ResourceAddress("c", "a"), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = "c", Name = "a", VersionId = "v1" });
        repository.Setup(r => r.CopyResourceAsync(It.IsAny<ResourceAddress>(), "v1", It.IsAny<NewResourceVersion>(),
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
        repository.Setup(r => r.MoveResourceAsync(It.IsAny<ResourceAddress>(), It.IsAny<ResourceAddress>(),
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
        repository.Setup(r => r.DeleteExpiredVersionsAsync(It.IsAny<IReadOnlyList<ResourceVersionAddress>>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The batch failed."));
        repository.Setup(r => r.DeleteExpiredVersionAsync(It.IsAny<ResourceAddress>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        // Act
        long deleted = await NewService(repository.Object).DeleteExpiredAsync();

        // Assert
        Assert.Equal(2, deleted);
        foreach (string id in new[] { "a", "b" })
        {
            repository.Verify(r => r.DeleteExpiredVersionAsync(new ResourceAddress("c", id), id,
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
        repository.Setup(r => r.GetResourceStreamAsync(new ResourceAddress("name", "a"), "v1", It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream([1]));
        var service = NewService(repository.Object);

        // Act
        await service.ExportContainerAsync("name", new MemoryStream(), cts.Token);

        // Assert: the container, its page of names' versions, and each version's content.
        repository.Verify(r => r.GetContainerInfoAsync("name", cts.Token), Times.Once);
        repository.Verify(r => r.GetVersionPageAsync("name", null, It.IsAny<int>(), cts.Token), Times.Once);
        repository.Verify(r => r.GetResourceStreamAsync(new ResourceAddress("name", "a"), "v1", cts.Token), Times.Once);
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
        repository.Setup(r => r.GetResourceStreamAsync(It.Is<ResourceAddress>(l => l.ContainerName == "c"), It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream([1]));

        // Act
        ContainerExportResult exported =
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
        new() { ContentLength = 5, ContentHash = "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", LastModified = DateTimeOffset.UtcNow };

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
            .Callback((NewResourceVersion version, Stream _, CancellationToken _) => steps.Add("version: " + version.Resource.ResourceName))
            .ReturnsAsync(Hello);
        var clock = new Testing.ManualTimeProvider { Now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero) };
        var client = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()), timeProvider: clock);

        // Act
        await client.ImportContainerAsync("d", await OneFileExportAsync());

        // Assert: with the time it was marked, so a copy can tell whether the import is still running.
        Assert.Equal(["metadata: resourcefull.import=in-progress, resourcefull.import_at=2026-11-04T12:00:00.0000000+00:00, team=billing",
            "version: a", "metadata: team=billing"], steps);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldWriteItsMarkAgainAndFinish_WhenARewriteOfTheMarkTimesOut()
    {
        // Arrange: an import whose one version waits while the clock moves, and a provider whose first rewrite of the mark
        // times out, as an HTTP client's request does.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var marks = new List<string>();
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.SetContainerMetadataAsync("d", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, IReadOnlyDictionary<string, string> metadata, CancellationToken _) =>
            {
                lock (marks) marks.Add(metadata.GetValueOrDefault("resourcefull.import_at") ?? "cleared");
                return marks.Count == 2 ? Task.FromException<bool>(new TaskCanceledException("Timed out.")) : Task.FromResult(true);
            });
        repository.Setup(r => r.ImportVersionAsync(It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Hello);
        var client = new ResourceServiceClient(repository.Object, Options.Create(new ResourcefullOptions()), timeProvider: time);
        var release = new TaskCompletionSource();
        var version = new ResourceInfo { ContainerName = "", Name = "a", VersionId = "v1", LastModified = DateTimeOffset.UnixEpoch };
        ImportedVersion[] versions = [new() { Version = version, OpenContentAsync = async _ =>
        {
            await release.Task;
            return new MemoryStream("hello"u8.ToArray());
        } }];

        // Act: the clock moves on until the mark has been written again after the rewrite that timed out.
        Task<ContainerImportResult> importing = client.ImportContainerAsync("d", versions.ToAsyncEnumerable());
        for (int turn = 0; turn < 1000 && marks.Count < 3; turn++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(5);
        }
        release.SetResult();
        ContainerImportResult imported = await importing;

        // Assert: marked, rewritten after the timeout, then cleared once the version was stored.
        Assert.Equal(1, imported.VersionCount);
        Assert.True(marks.Count >= 4);
        Assert.Equal("cleared", marks[^1]);
        Assert.True(DateTimeOffset.Parse(marks[2]) > DateTimeOffset.Parse(marks[0]));
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
        Assert.Equal(new Dictionary<string, string> { ["team"] = "billing" }, (await service.GetContainerClient("restored").GetInfoAsync())!.Metadata);
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
        ContainerImportResult imported = await client.ImportContainerAsync("d", await OneFileExportAsync());

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
                return new WrittenContent
                {
                    ContentLength = 5,
                    ContentHash = "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                    LastModified = DateTimeOffset.UtcNow
                };
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
            .ReturnsAsync(new WrittenContent
            {
                ContentLength = 1,
                ContentHash = hash,
                LastModified = DateTimeOffset.UnixEpoch
            });
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
