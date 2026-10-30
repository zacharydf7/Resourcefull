using System.IO.Compression;
using System.Security.Cryptography;
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
    public void GetResourceContainerClient_ShouldCarryTheUriProvidersUri_WhenAUriProviderIsConfigured()
    {
        // Arrange
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(new ResourcefullContext("myContainer", null, null))).Returns("https://resources.test/myContainer");
        var service = NewService(new Mock<IResourceRepository>(MockBehavior.Strict).Object, uriProvider.Object);

        // Act
        IResourceContainerClient client = service.GetResourceContainerClient("myContainer");

        // Assert
        Assert.Equal("https://resources.test/myContainer", client.ResourceContainerInfo.Uri);
    }

    private static IUriProvider NewBaseUrlUriProvider()
    {
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(It.IsAny<ResourcefullContext>()))
            .Returns((ResourcefullContext context) => $"https://new.test/{context.ContainerName}");
        return uriProvider.Object;
    }

    private static Mock<IResourceRepository> NewRepositoryWithStoredUris()
    {
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceContainerInfoAsync("stored", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceContainerInfo { Name = "stored", Uri = "https://old.test/stored" });
        repository.Setup(r => r.GetAllResourceContainerInfoAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new ResourceContainerInfo { Name = "first", Uri = "https://old.test/first" },
            new ResourceContainerInfo { Name = "second", Uri = "https://old.test/second" }
        ]);
        return repository;
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldReturnTheUriProvidersUri_WhenTheStoredUriIsOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        ResourceContainerInfo? containerInfo = await service.GetResourceContainerInfoAsync("stored");

        // Assert
        Assert.Equal("https://new.test/stored", containerInfo!.Uri);
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldReturnNoUri_WhenNoUriProviderIsConfigured()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object);

        // Act
        ResourceContainerInfo? containerInfo = await service.GetResourceContainerInfoAsync("stored");

        // Assert
        Assert.Null(containerInfo!.Uri);
    }

    [Fact]
    public async Task GetResourceContainersAsync_ShouldReturnTheUriProvidersUris_WhenTheStoredUrisAreOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        IReadOnlyList<ResourceContainerInfo> containers = await service.GetResourceContainersAsync();

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
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldReturnTheUriProvidersUri_WhenTheExistingContainersStoredUriIsOutOfDate()
    {
        // Arrange
        var service = NewService(NewRepositoryWithStoredUris().Object, NewBaseUrlUriProvider());

        // Act
        IResourceContainerClient client = await service.CreateResourceContainerIfNotExistsAsync("stored");

        // Assert
        Assert.Equal("https://new.test/stored", client.ResourceContainerInfo.Uri);
    }

    [Fact]
    public async Task CreateResourceContainerAsync_ShouldCreateContainer_WhenItDoesNotExist()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
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
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(() => service.CreateResourceContainerAsync("existing"));
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
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldReturnAClientWithTheStoredInfo_WhenAConcurrentCreateWinsTheAdd()
    {
        // Arrange: absent when checked, then created by someone else, with their metadata, before this add.
        var repository = new Mock<IResourceRepository>();
        repository.SetupSequence(r => r.GetResourceContainerInfoAsync("existing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResourceContainerInfo?)null)
            .ReturnsAsync(new ResourceContainerInfo { Name = "existing", Uri = "https://old.test/existing", Metadata = new Dictionary<string, string> { ["owner"] = "them" } });
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object, NewBaseUrlUriProvider());

        // Act
        IResourceContainerClient client = await service.CreateResourceContainerIfNotExistsAsync("existing", new Dictionary<string, string> { ["owner"] = "us" });

        // Assert
        Assert.Equal("them", client.ResourceContainerInfo.Metadata["owner"]);
        Assert.Equal("https://new.test/existing", client.ResourceContainerInfo.Uri);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldReturnAClientForTheName_WhenTheConcurrentlyCreatedContainerIsGoneAgain()
    {
        // Arrange: created by someone else before this add, and deleted again before it can be read.
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("already exists"));
        var service = NewService(repository.Object);

        // Act
        IResourceContainerClient client = await service.CreateResourceContainerIfNotExistsAsync("existing", new Dictionary<string, string> { ["owner"] = "us" });

        // Assert
        Assert.Equal("existing", client.ResourceContainerInfo.Name);
        Assert.Empty(client.ResourceContainerInfo.Metadata);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldCreate_WhenContainerDoesNotExist()
    {
        // Arrange
        var repository = new Mock<IResourceRepository>();
        var service = NewService(repository.Object);

        // Act
        await service.CreateResourceContainerIfNotExistsAsync("fresh");

        // Assert
        repository.Verify(r => r.AddResourceContainerAsync(It.Is<ResourceContainerInfo>(c => c.Name == "fresh"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldRethrow_WhenTheAddFailsForAnotherReason()
    {
        // Arrange & Act
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage down"));
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateResourceContainerIfNotExistsAsync("boom"));
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldReportCancellation_WhenTheAddIsCanceled()
    {
        // Arrange & Act
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var service = NewService(repository.Object);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CreateResourceContainerIfNotExistsAsync("canceled"));
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
        var service = NewService(repository.Object);

        // Act
        await service.CreateResourceContainerAsync("name", null, cts.Token);

        // Assert
        repository.Verify(r => r.AddResourceContainerAsync(It.IsAny<ResourceContainerInfo>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task CreateResourceContainerIfNotExistsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
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

    [Fact]
    public async Task DeleteExpiredAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        var expired = new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "v1", ExpiresAt = DateTimeOffset.UnixEpoch };
        repository.Setup(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>())).ReturnsAsync([expired]);
        repository.Setup(r => r.DeleteExpiredVersionAsync(It.IsAny<ResourceLocation>(), "v1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var service = NewService(repository.Object);

        // Act
        await service.DeleteExpiredAsync(cts.Token);

        // Assert: the check for any, the batch, and the delete.
        repository.Verify(r => r.GetExpiredAsync(It.IsAny<DateTimeOffset>(), It.IsAny<int>(), null, cts.Token), Times.Exactly(2));
        repository.Verify(r => r.DeleteExpiredVersionAsync(new ResourceLocation("c", "n"), "v1", It.IsAny<DateTimeOffset>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task ExportContainerAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        var version = new ResourceInfo { ContainerName = "name", Name = "a", VersionId = "v1" };
        repository.Setup(r => r.GetResourceContainerInfoAsync("name", It.IsAny<CancellationToken>())).ReturnsAsync(new ResourceContainerInfo { Name = "name" });
        repository.Setup(r => r.GetAllResourceInfoAsync("name", It.IsAny<CancellationToken>())).ReturnsAsync([version]);
        repository.Setup(r => r.GetResourceVersionsAsync("name", "a", It.IsAny<CancellationToken>())).ReturnsAsync([version]);
        repository.Setup(r => r.GetResourceStreamAsync("name", "a", "v1", It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream([1]));
        var service = NewService(repository.Object);

        // Act
        await service.ExportContainerAsync("name", new MemoryStream(), cts.Token);

        // Assert: the container, its names, each name's versions, and each version's content.
        repository.Verify(r => r.GetResourceContainerInfoAsync("name", cts.Token), Times.Once);
        repository.Verify(r => r.GetAllResourceInfoAsync("name", cts.Token), Times.Once);
        repository.Verify(r => r.GetResourceVersionsAsync("name", "a", cts.Token), Times.Once);
        repository.Verify(r => r.GetResourceStreamAsync("name", "a", "v1", cts.Token), Times.Once);
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange: the export of a container holding one version of "a".
        using var cts = new CancellationTokenSource();
        var repository = new Mock<IResourceRepository>();
        string hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData([1]));
        repository.Setup(r => r.ImportVersionAsync("name", It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
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
        repository.Verify(r => r.AddResourceContainerAsync(It.Is<ResourceContainerInfo>(c => c.Name == "name"), cts.Token), Times.Once);
        repository.Verify(r => r.ImportVersionAsync("name", It.Is<ResourceInfo>(v => v.VersionId == "v1"), It.IsAny<Stream>(), cts.Token), Times.Once);
    }
}
