using Moq;
using System.Text;

namespace Resourcefull.Tests.Unit;

public class ResourceContainerClientTests
{
    private const string Container = "containerName";

    private static ResourceContainerClient NewClient(
        IResourceRepository repository,
        string containerName = Container,
        ResourcefullOptions? options = null,
        IUriProvider? uriProvider = null)
        => new(repository, uriProvider, options ?? new ResourcefullOptions(),
            new ResourceContainerInfo { Name = containerName });

    private static Mock<IResourceRepository> NewRepository()
    {
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return repository;
    }

    [Fact]
    public async Task UploadAsync_ShouldReturnResourceInfo_WhenNewResourceIsUploaded()
    {
        // Arrange
        var name = "newResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, new MemoryStream());

        // Assert
        Assert.Equal(Container, result.ContainerName);
        Assert.Equal(name, result.Name);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldStampUri_WhenUriProviderIsConfigured()
    {
        // Arrange
        var name = "newResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(u => u.GetAbsoluteUri(It.IsAny<ResourcefullContext>()))
            .Returns("http://resources/x");
        var client = NewClient(repository.Object, uriProvider: uriProvider.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, new MemoryStream());

        // Assert
        Assert.Equal("http://resources/x", result.Uri);
    }

    [Fact]
    public async Task UploadAsync_ShouldUploadResource_WhenContentIsBinaryData()
    {
        // Arrange
        var name = "newResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, BinaryData.FromString("hello"));

        // Assert
        Assert.Equal(name, result.Name);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowConflict_WhenResourceExistsAndOverwriteIsFalse()
    {
        // Arrange & Act
        var name = "existingResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = name });
        var client = NewClient(repository.Object);

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(
            () => client.UploadAsync(name, new MemoryStream()));
    }

    [Fact]
    public async Task UploadAsync_ShouldSucceed_WhenResourceExistsAndOverwriteIsTrue()
    {
        // Arrange
        var name = "existingResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = name });
        repository.Setup(r => r.GetResourceVersionsAsync(Container, name,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { ContainerName = Container, Name = name, VersionId = "v1" }]);
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, new MemoryStream(),
            new ResourceUploadOptions { Overwrite = true });

        // Assert
        Assert.Equal(name, result.Name);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.DeleteResourceAsync(Container, name, "v1",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UploadAsync_ShouldThrowArgumentException_WhenNameIsBlank(string name)
    {
        // Arrange & Act
        var client = NewClient(new Mock<IResourceRepository>().Object);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.UploadAsync(name, new MemoryStream()));
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowArgumentNullException_WhenContentIsNull()
    {
        // Arrange & Act
        var client = NewClient(new Mock<IResourceRepository>().Object);

        // Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.UploadAsync("name", (Stream)null!));
    }

    [Fact]
    public async Task UploadAsync_ShouldDeleteOldestVersionAfterStoringTheNewOne_WhenMaxVersionsIsReached()
    {
        // Arrange
        var name = "existingResourceName";
        var versionId = "2025-05-14T12:00:00.0000000Z";
        var calls = new List<string>();
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, name,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = name, VersionId = versionId }
            ]);
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("add")).Returns(Task.CompletedTask);
        repository.Setup(r => r.DeleteResourceAsync(Container, name, versionId,
            It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("delete")).ReturnsAsync(true);
        var options = new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 1 };
        var client = NewClient(repository.Object, options: options);

        // Act
        await client.UploadAsync(name, new MemoryStream());

        // Assert
        Assert.Equal(["add", "delete"], calls);
    }

    [Fact]
    public async Task UploadAsync_ShouldDeleteEveryExcessVersion_WhenMoreThanMaxVersionsAlreadyExist()
    {
        // Arrange
        var name = "existingResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, name,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = name, VersionId = "v3" },
                new() { ContainerName = Container, Name = name, VersionId = "v2" },
                new() { ContainerName = Container, Name = name, VersionId = "v1" }
            ]);
        var options = new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 2 };
        var client = NewClient(repository.Object, options: options);

        // Act
        await client.UploadAsync(name, new MemoryStream());

        // Assert
        repository.Verify(r => r.DeleteResourceAsync(Container, name, "v1",
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.DeleteResourceAsync(Container, name, "v2",
            It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.DeleteResourceAsync(Container, name, "v3",
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadAsync_ShouldNotDeleteOldestVersion_WhenMaxVersionsIsNotReached()
    {
        // Arrange
        var name = "existingResourceName";
        var versionId = "2025-05-14T12:00:00.0000000Z";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, name,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = name, VersionId = versionId }
            ]);
        var options = new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 2 };
        var client = NewClient(repository.Object, options: options);

        // Act
        await client.UploadAsync(name, new MemoryStream());

        // Assert
        repository.Verify(r => r.DeleteResourceAsync(Container, name, It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadAsync_ShouldNotPrune_WhenTheWriteFails()
    {
        // Arrange
        var name = "existingResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, name,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { ContainerName = Container, Name = name, VersionId = "v1" }]);
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var options = new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 1 };
        var client = NewClient(repository.Object, options: options);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.UploadAsync(name, new MemoryStream()));

        // Assert
        repository.Verify(r => r.DeleteResourceAsync(Container, name, It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldReturnResult_WhenResourceExists()
    {
        // Arrange
        var name = "existingResourceName";
        var stream = new MemoryStream();
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = name, VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, name, "v1",
            It.IsAny<CancellationToken>())).ReturnsAsync(stream);
        var client = NewClient(repository.Object);

        // Act
        ResourceDownloadResult? result = await client.DownloadStreamingAsync(name);

        // Assert
        Assert.NotNull(result);
        Assert.Same(stream, result!.Stream);
        Assert.Equal(name, result.ResourceInfo.Name);
    }

    [Fact]
    public async Task DownloadContentAsync_ShouldReturnTheResolvedVersionsContent_WhenCurrentChangesBetweenLookups()
    {
        // Arrange: info resolves "current" to v1, then an upload makes v2 current before the content is read.
        var name = "name";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = name, VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, name, null,
            It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("v2 content")));
        repository.Setup(r => r.GetResourceStreamAsync(Container, name, "v1",
            It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("v1 content")));
        var client = NewClient(repository.Object);

        // Act
        ResourceContent? content = await client.DownloadContentAsync(name);

        // Assert
        Assert.Equal("v1", content!.ResourceInfo.VersionId);
        Assert.Equal("v1 content", content.Content.ToString());
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldReturnNull_WhenResolvedVersionDisappearsBeforeContentIsRead()
    {
        // Arrange: info resolves "current" to v1, then v1 is deleted before its content is read.
        var name = "name";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = name, VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, name, null,
            It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("v2 content")));
        repository.Setup(r => r.GetResourceStreamAsync(Container, name, "v1",
            It.IsAny<CancellationToken>())).ReturnsAsync((Stream?)null);
        var client = NewClient(repository.Object);

        // Act
        ResourceDownloadResult? result = await client.DownloadStreamingAsync(name);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldReturnNull_WhenResourceDoesNotExist()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "missing", null,
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var client = NewClient(repository.Object);

        // Act
        ResourceDownloadResult? result = await client.DownloadStreamingAsync("missing");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadContentAsync_ShouldReturnContent_WhenResourceExists()
    {
        // Arrange
        var name = "existingResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = name, VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, name, "v1",
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        var client = NewClient(repository.Object);

        // Act
        ResourceContent? content = await client.DownloadContentAsync(name);

        // Assert
        Assert.NotNull(content);
        Assert.Equal("hello", content!.Content.ToString());
    }

    [Fact]
    public async Task DownloadContentAsync_ShouldReturnNull_WhenResourceDoesNotExist()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "missing", null,
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var client = NewClient(repository.Object);

        // Act
        ResourceContent? content = await client.DownloadContentAsync("missing");

        // Assert
        Assert.Null(content);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldReturnInfo_WhenExists()
    {
        // Arrange
        var name = "resourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = name });
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo? info = await client.GetResourceInfoAsync(name);

        // Assert
        Assert.Equal(name, info!.Name);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldReturnNull_WhenNotFound()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "missing", null,
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo? info = await client.GetResourceInfoAsync("missing");

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public async Task GetResourcesAsync_ShouldReturnAllResources_WhenRepositoryHoldsResources()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetAllResourceInfoAsync(Container, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = "a" },
                new() { ContainerName = Container, Name = "b" }
            ]);
        var client = NewClient(repository.Object);

        // Act
        IReadOnlyList<ResourceInfo> resources = await client.GetResourcesAsync();

        // Assert
        Assert.Equal(2, resources.Count);
    }

    [Fact]
    public async Task GetResourceVersionsAsync_ShouldReturnAllVersions_WhenMultipleVersionsExist()
    {
        // Arrange
        var name = "resourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, name,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = name, VersionId = "v2", IsLatest = true },
                new() { ContainerName = Container, Name = name, VersionId = "v1" }
            ]);
        var client = NewClient(repository.Object);

        // Act
        IReadOnlyList<ResourceInfo> versions = await client.GetResourceVersionsAsync(name);

        // Assert
        Assert.Equal(2, versions.Count);
        Assert.True(versions[0].IsLatest);
    }

    [Fact]
    public async Task ExistsAsync_ShouldReflectRepository_WhenResourceIsPresentOrMissing()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "here", null,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "here" });
        repository.Setup(r => r.GetResourceInfoAsync(Container, "gone", null,
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var client = NewClient(repository.Object);

        // Act
        bool present = await client.ExistsAsync("here");
        bool missing = await client.ExistsAsync("gone");

        // Assert
        Assert.True(present);
        Assert.False(missing);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldReturnTrue_WhenRepositoryDeletesTheResource()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.DeleteResourceAsync(Container, "x", null,
            It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var client = NewClient(repository.Object);

        // Act
        bool deleted = await client.DeleteResourceIfExistsAsync("x");

        // Assert
        Assert.True(deleted);
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldReturnNewVersion_WhenVersionExists()
    {
        // Arrange
        var name = "resourceName";
        var versionId = "2024-05-14T12:00:00.0000000Z";
        var existing = new ResourceInfo
        {
            ContainerName = Container,
            Name = name,
            VersionId = versionId
        };
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, name, versionId,
            It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        repository.Setup(r => r.GetResourceStreamAsync(Container, name, versionId,
            It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream());
        var options = new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 10 };
        var client = NewClient(repository.Object, options: options);

        // Act
        ResourceInfo? result = await client.RestoreVersionAsync(name, versionId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(name, result!.Name);
        Assert.NotEqual(versionId, result.VersionId);
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldThrowInvalidOperation_WhenVersioningDisabled()
    {
        // Arrange & Act
        var client = NewClient(new Mock<IResourceRepository>().Object);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.RestoreVersionAsync("name", "v1"));
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldReturnNull_WhenVersionDoesNotExist()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "name", "v1",
            It.IsAny<CancellationToken>())).ReturnsAsync((ResourceInfo?)null);
        var options = new ResourcefullOptions { IsVersioningEnabled = true };
        var client = NewClient(repository.Object, options: options);

        // Act
        ResourceInfo? result = await client.RestoreVersionAsync("name", "v1");

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("", "v1")]
    [InlineData("name", "")]
    public async Task RestoreVersionAsync_ShouldThrowArgumentException_WhenArgsBlank(
        string name,
        string versionId)
    {
        // Arrange & Act
        var options = new ResourcefullOptions { IsVersioningEnabled = true };
        var client = NewClient(new Mock<IResourceRepository>().Object, options: options);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.RestoreVersionAsync(name, versionId));
    }

    [Fact]
    public async Task UploadAsync_ShouldPassCancellationTokenNone_WhenCalledWithoutAToken()
    {
        // Arrange
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.UploadAsync("name", new MemoryStream());

        // Assert
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldPassTheToken_WhenContentIsAStream()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.UploadAsync("name", new MemoryStream(), null, cts.Token);

        // Assert
        repository.Verify(r => r.GetResourceInfoAsync(Container, "name", null, cts.Token),
            Times.Once);
        repository.Verify(r => r.GetResourceVersionsAsync(Container, "name", cts.Token),
            Times.Once);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldPassTheToken_WhenContentIsBinaryData()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.UploadAsync("name", BinaryData.FromString("hello"), null, cts.Token);

        // Assert
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldPassTheToken_WhenVersionExists()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "name", "v1", cts.Token))
            .ReturnsAsync(new ResourceInfo
            {
                ContainerName = Container,
                Name = "name",
                VersionId = "v1"
            });
        repository.Setup(r => r.GetResourceStreamAsync(Container, "name", "v1", cts.Token))
            .ReturnsAsync(new MemoryStream());
        var client = NewClient(repository.Object,
            options: new ResourcefullOptions { IsVersioningEnabled = true });

        // Act
        await client.RestoreVersionAsync("name", "v1", cts.Token);

        // Assert
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), cts.Token), Times.Once);
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldPassTheToken_WhenResourceExists()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "name", null, cts.Token))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "name", VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, "name", "v1", cts.Token))
            .ReturnsAsync(new MemoryStream());
        var client = NewClient(repository.Object);

        // Act
        ResourceDownloadResult? result =
            await client.DownloadStreamingAsync("name", null, cts.Token);

        // Assert
        Assert.NotNull(result);
        repository.Verify(r => r.GetResourceStreamAsync(Container, "name", "v1", cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task DownloadContentAsync_ShouldPassTheToken_WhenResourceExists()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "name", null, cts.Token))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "name", VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, "name", "v1", cts.Token))
            .ReturnsAsync(new MemoryStream());
        var client = NewClient(repository.Object);

        // Act
        ResourceContent? content = await client.DownloadContentAsync("name", null, cts.Token);

        // Assert
        Assert.NotNull(content);
        repository.Verify(r => r.GetResourceStreamAsync(Container, "name", "v1", cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.GetResourceInfoAsync("name", "v1", cts.Token);

        // Assert
        repository.Verify(r => r.GetResourceInfoAsync(Container, "name", "v1", cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task GetResourcesAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        repository.Setup(r => r.GetAllResourceInfoAsync(Container, cts.Token)).ReturnsAsync([]);
        var client = NewClient(repository.Object);

        // Act
        await client.GetResourcesAsync(cts.Token);

        // Assert
        repository.Verify(r => r.GetAllResourceInfoAsync(Container, cts.Token), Times.Once);
    }

    [Fact]
    public async Task GetResourceVersionsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.GetResourceVersionsAsync("name", cts.Token);

        // Assert
        repository.Verify(r => r.GetResourceVersionsAsync(Container, "name", cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.DeleteResourceIfExistsAsync("name", "v1", cts.Token);

        // Assert
        repository.Verify(r => r.DeleteResourceAsync(Container, "name", "v1", cts.Token),
            Times.Once);
    }

    [Fact]
    public async Task ExistsAsync_ShouldPassTheToken_WhenCalledWithOne()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.ExistsAsync("name", null, cts.Token);

        // Assert
        repository.Verify(r => r.GetResourceInfoAsync(Container, "name", null, cts.Token),
            Times.Once);
    }
}
