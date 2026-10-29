using Microsoft.Extensions.Logging;
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
        IUriProvider? uriProvider = null,
        ILogger? logger = null)
        => new(repository, uriProvider, options ?? new ResourcefullOptions(),
            new ResourceContainerInfo { Name = containerName }, logger);

    private static readonly WrittenContent Written = new(5, "sha256:written", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static Mock<IResourceRepository> NewRepository()
    {
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Written);
        return repository;
    }

    public static TheoryData<string?> InvalidResourceNames() =>
    [
        null,
        "",
        "   ",
        " leading",
        "trailing ",
        new string('a', ResourceNames.MaxResourceNameLength + 1)
    ];

    [Theory]
    [MemberData(nameof(InvalidResourceNames))]
    public async Task EveryResourceNameEntryPoint_ShouldThrowArgumentException_WhenGivenAnInvalidName(string? name)
    {
        // Arrange
        var repository = new Mock<IResourceRepository>(MockBehavior.Strict);
        var client = NewClient(repository.Object, options: new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 1 });

        // Act & Assert
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.UploadAsync(name!, new MemoryStream()));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.UploadAsync(name!, BinaryData.FromString("x")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.RestoreVersionAsync(name!, "v1"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.DownloadStreamingAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.DownloadContentAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetResourceInfoAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetResourceVersionsAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.DeleteResourceIfExistsAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.ExistsAsync(name!));
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreTheResource_WhenGivenANameAtTheMaximumLength()
    {
        // Arrange
        var name = new string('a', ResourceNames.MaxResourceNameLength);
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        await client.UploadAsync(name, new MemoryStream());

        // Assert
        repository.Verify(r => r.AddResourceAsync(Container, It.Is<ResourceInfo>(i => i.Name == name),
            It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldReturnResourceInfo_WhenNewResourceIsUploaded()
    {
        // Arrange
        var name = "newResourceName";
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, new MemoryStream());

        // Assert
        Assert.Equal(Container, result.ContainerName);
        Assert.Equal(name, result.Name);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldStampUri_WhenUriProviderIsConfigured()
    {
        // Arrange
        var name = "newResourceName";
        var repository = NewRepository();
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(u => u.GetAbsoluteUri(It.IsAny<ResourcefullContext>()))
            .Returns("http://resources/x");
        var client = NewClient(repository.Object, uriProvider: uriProvider.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, new MemoryStream());

        // Assert
        Assert.Equal("http://resources/x", result.Uri);
    }

    private static IUriProvider NewBaseUrlUriProvider()
    {
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(It.IsAny<ResourcefullContext>())).Returns((ResourcefullContext context)
            => $"https://new.test/{context.ContainerName}/{context.ResourceName}/{context.ResourceVersionId}");
        return uriProvider.Object;
    }

    private static ResourceInfo StoredVersion(string versionId) => new()
    {
        ContainerName = Container,
        Name = "report.pdf",
        VersionId = versionId,
        Uri = $"https://old.test/{Container}/report.pdf/{versionId}"
    };

    private static Mock<IResourceRepository> NewRepositoryWithStoredUris()
    {
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "report.pdf", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => StoredVersion("v2"));
        repository.Setup(r => r.GetResourceStreamAsync(Container, "report.pdf", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream());
        repository.Setup(r => r.GetAllResourceInfoAsync(Container, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [StoredVersion("v2")]);
        repository.Setup(r => r.GetResourceVersionsAsync(Container, "report.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [StoredVersion("v2"), StoredVersion("v1")]);
        return repository;
    }

    [Fact]
    public async Task EveryReadOfAVersion_ShouldReturnTheUriProvidersUri_WhenTheStoredUriIsOutOfDate()
    {
        // Arrange
        var client = NewClient(NewRepositoryWithStoredUris().Object, uriProvider: NewBaseUrlUriProvider());
        string current = $"https://new.test/{Container}/report.pdf/v2";

        // Act & Assert
        Assert.Equal(current, (await client.GetResourceInfoAsync("report.pdf"))!.Uri);
        Assert.Equal(current, (await client.DownloadStreamingAsync("report.pdf"))!.ResourceInfo.Uri);
        Assert.Equal(current, (await client.DownloadContentAsync("report.pdf"))!.ResourceInfo.Uri);
        Assert.Equal([current], (await client.GetResourcesAsync()).Select(r => r.Uri));
        Assert.Equal([current, $"https://new.test/{Container}/report.pdf/v1"],
            (await client.GetResourceVersionsAsync("report.pdf")).Select(r => r.Uri));
    }

    [Fact]
    public async Task DownloadStreamingAsync_ShouldNotOpenTheStream_WhenTheUriProviderThrows()
    {
        // Arrange
        var repository = NewRepositoryWithStoredUris();
        var uriProvider = new Mock<IUriProvider>();
        uriProvider.Setup(p => p.GetAbsoluteUri(It.IsAny<ResourcefullContext>())).Throws<InvalidOperationException>();
        var client = NewClient(repository.Object, uriProvider: uriProvider.Object);

        // Act
        Task act() => client.DownloadStreamingAsync("report.pdf");

        // Assert: a stream opened here would never reach the caller to be disposed.
        await Assert.ThrowsAsync<InvalidOperationException>(act);
        repository.Verify(r => r.GetResourceStreamAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldReturnNoUri_WhenNoUriProviderIsConfigured()
    {
        // Arrange
        var client = NewClient(NewRepositoryWithStoredUris().Object);

        // Act
        ResourceInfo? resourceInfo = await client.GetResourceInfoAsync("report.pdf");

        // Assert
        Assert.Null(resourceInfo!.Uri);
    }

    [Fact]
    public async Task UploadAsync_ShouldUploadResource_WhenContentIsBinaryData()
    {
        // Arrange
        var name = "newResourceName";
        var repository = NewRepository();
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, BinaryData.FromString("hello"));

        // Assert
        Assert.Equal(name, result.Name);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowConflict_WhenResourceExistsAndOverwriteIsFalse()
    {
        // Arrange & Act
        var name = "existingResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(),
            true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourceConflictException("exists"));
        var client = NewClient(repository.Object);

        // Assert
        var exception = await Assert.ThrowsAsync<ResourceConflictException>(
            () => client.UploadAsync(name, new MemoryStream()));
        Assert.Contains(nameof(ResourceUploadOptions.Overwrite), exception.Message);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, true)]
    public async Task UploadAsync_ShouldWriteCreateOnly_WhenTheOptionsForbidReplacingAnExistingName(
        bool versioning, bool overwrite, bool createOnly, bool expectedCreateOnly)
    {
        // Arrange
        var repository = NewRepository();
        var client = NewClient(repository.Object, options: new ResourcefullOptions { IsVersioningEnabled = versioning, MaxVersions = 10 });

        // Act
        await client.UploadAsync("name", new MemoryStream(), new ResourceUploadOptions { Overwrite = overwrite, CreateOnly = createOnly });

        // Assert
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(),
            expectedCreateOnly, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldSucceed_WhenResourceExistsAndOverwriteIsTrue()
    {
        // Arrange: the versions as they stand after the write, newest first.
        var name = "existingResourceName";
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, name,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = name, VersionId = "new" },
                new() { ContainerName = Container, Name = name, VersionId = "v1" }
            ]);
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo result = await client.UploadAsync(name, new MemoryStream(),
            new ResourceUploadOptions { Overwrite = true });

        // Assert
        Assert.Equal(name, result.Name);
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(r => r.DeleteResourceAsync(Container, name, "v1",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task UploadAsync_ShouldThrowResourceNameException_WhenNameIsBlank(string name)
    {
        // Arrange & Act
        var client = NewClient(new Mock<IResourceRepository>().Object);

        // Assert
        await Assert.ThrowsAsync<ResourceNameException>(
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
                new() { ContainerName = Container, Name = name, VersionId = "new" },
                new() { ContainerName = Container, Name = name, VersionId = versionId }
            ]);
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("add")).ReturnsAsync(Written);
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
                new() { ContainerName = Container, Name = name, VersionId = "new" },
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
        repository.Verify(r => r.DeleteResourceAsync(Container, name, "new",
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
                new() { ContainerName = Container, Name = name, VersionId = "new" },
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
            It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task RestoreVersionAsync_ShouldThrowResourceNameException_WhenTheNameIsBlank()
    {
        // Arrange & Act
        var options = new ResourcefullOptions { IsVersioningEnabled = true };
        var client = NewClient(new Mock<IResourceRepository>().Object, options: options);

        // Assert
        await Assert.ThrowsAsync<ResourceNameException>(() => client.RestoreVersionAsync("", "v1"));
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldThrowArgumentException_WhenTheVersionIdIsBlank()
    {
        // Arrange & Act
        var options = new ResourcefullOptions { IsVersioningEnabled = true };
        var client = NewClient(new Mock<IResourceRepository>().Object, options: options);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>("versionId", () => client.RestoreVersionAsync("name", ""));
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
            It.IsAny<Stream>(), It.IsAny<bool>(), CancellationToken.None), Times.Once);
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
        repository.Verify(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(),
            It.IsAny<Stream>(), It.IsAny<bool>(), cts.Token), Times.Once);
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
            It.IsAny<Stream>(), It.IsAny<bool>(), cts.Token), Times.Once);
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
            It.IsAny<Stream>(), It.IsAny<bool>(), cts.Token), Times.Once);
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
    public async Task DownloadContentAsync_ShouldStopCopyingAndReleaseTheStream_WhenCancelledMidCopy()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var content = new CancelAfterReadsStream(new byte[1024 * 1024], cts, readsBeforeCancel: 1);
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "name", null, cts.Token))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "name", VersionId = "v1" });
        repository.Setup(r => r.GetResourceStreamAsync(Container, "name", "v1", cts.Token)).ReturnsAsync(content);
        var client = NewClient(repository.Object);

        // Act
        Task act() => client.DownloadContentAsync("name", null, cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.False(content.CanRead);
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

    [Fact]
    public async Task UploadAsync_ShouldAssignADistinctVersion7GuidToEachVersion_WhenCalledRepeatedly()
    {
        // Arrange
        var stored = new List<string?>();
        var repository = NewRepository();
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, ResourceInfo, Stream, bool, CancellationToken>((_, info, _, _, _) => stored.Add(info.VersionId))
            .ReturnsAsync(Written);
        var client = NewClient(repository.Object, options: new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 100 });

        // Act
        for (var i = 0; i < 50; i++)
        {
            await client.UploadAsync("name", new MemoryStream());
        }

        // Assert
        Assert.Equal(50, stored.Distinct().Count());
        Assert.All(stored, id => Assert.Equal(7, Guid.Parse(id!).Version));
    }

    [Fact]
    public async Task UploadAsync_ShouldReturnTheStoredVersionAndStillPrune_WhenCancelledAfterTheWrite()
    {
        // Arrange: overwrite without versioning; the caller cancels as soon as the new version is stored.
        using var cts = new CancellationTokenSource();
        var repository = NewRepository();
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(cts.Cancel)
            .ReturnsAsync(Written);
        repository.Setup(r => r.GetResourceVersionsAsync(Container, "name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = "name", VersionId = "new" },
                new() { ContainerName = Container, Name = "name", VersionId = "old" }
            ]);
        var client = NewClient(repository.Object);

        // Act
        ResourceInfo result = await client.UploadAsync("name", new MemoryStream(), new ResourceUploadOptions { Overwrite = true }, cts.Token);

        // Assert
        Assert.Equal("name", result.Name);
        repository.Verify(r => r.DeleteResourceAsync(Container, "name", "old", CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldReturnTheStoredVersionAndLogAWarning_WhenPruningFails()
    {
        // Arrange
        var failure = new InvalidOperationException("storage unavailable");
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, "name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = "name", VersionId = "new" },
                new() { ContainerName = Container, Name = "name", VersionId = "old" }
            ]);
        repository.Setup(r => r.DeleteResourceAsync(Container, "name", "old", It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var client = NewClient(repository.Object, logger: logger.Object);

        // Act
        ResourceInfo result = await client.UploadAsync("name", new MemoryStream(), new ResourceUploadOptions { Overwrite = true });

        // Assert
        Assert.Equal("name", result.Name);
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), failure,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }

    [Fact]
    public async Task UploadAsync_ShouldReturnTheStoredVersion_WhenLoggingAPruneFailureThrows()
    {
        // Arrange
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceVersionsAsync(Container, "name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = Container, Name = "name", VersionId = "new" },
                new() { ContainerName = Container, Name = "name", VersionId = "old" }
            ]);
        repository.Setup(r => r.DeleteResourceAsync(Container, "name", "old", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("storage unavailable"));
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        logger.Setup(l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>())).Throws(new AggregateException("logger unavailable"));
        var client = NewClient(repository.Object, logger: logger.Object);

        // Act
        ResourceInfo result = await client.UploadAsync("name", new MemoryStream(), new ResourceUploadOptions { Overwrite = true });

        // Assert
        Assert.Equal("name", result.Name);
    }

    [Fact]
    public async Task UploadAsync_ShouldReturnWhatWasStored_WhenTheWriteSucceeds()
    {
        // Arrange: the caller declares nothing about the length and keeps its own metadata dictionary.
        var metadata = new Dictionary<string, string> { ["k"] = "v" };
        var client = NewClient(NewRepository().Object);

        // Act
        ResourceInfo result = await client.UploadAsync("name", new MemoryStream(), new ResourceUploadOptions { Metadata = metadata });

        // Assert
        Assert.Equal(Written.ContentLength, result.ContentLength);
        Assert.Equal(Written.ContentHash, result.ContentHash);
        Assert.Equal(Written.LastModified, result.LastModified);
        Assert.True(result.IsLatest);
        Assert.NotSame(metadata, result.Metadata);
        Assert.Equal(metadata, result.Metadata);
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldMeasureTheRestoredContent_WhenTheStoredLengthIsWrong()
    {
        // Arrange: an older version whose stored length came from a caller and is wrong.
        var repository = NewRepository();
        repository.Setup(r => r.GetResourceInfoAsync(Container, "name", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = Container, Name = "name", VersionId = "v1", ContentLength = 999 });
        repository.Setup(r => r.GetResourceStreamAsync(Container, "name", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream([1, 2, 3]));
        long? declaredLength = -1;
        repository.Setup(r => r.AddResourceAsync(Container, It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), false, It.IsAny<CancellationToken>()))
            .Callback<string, ResourceInfo, Stream, bool, CancellationToken>((_, info, _, _, _) => declaredLength = info.ContentLength)
            .ReturnsAsync(Written);
        var client = NewClient(repository.Object, options: new ResourcefullOptions { IsVersioningEnabled = true, MaxVersions = 10 });

        // Act
        ResourceInfo? restored = await client.RestoreVersionAsync("name", "v1");

        // Assert
        Assert.Null(declaredLength);
        Assert.Equal(Written.ContentLength, restored!.ContentLength);
        Assert.True(restored.IsLatest);
    }
}
