using System.Security.Cryptography;
using System.Text;
using Resourcefull.InMemory;
using Resourcefull.Tests.Contract;

namespace Resourcefull.Tests.Unit.InMemory;

public class ResourceInMemoryRepositoryTests
{
    [Fact]
    public async Task AddResourceAsync_ShouldAddToRepository_WhenContainerExists()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Act
        await repository.AddResourceAsync(resourceInfo, new MemoryStream());

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowContainerNotFound_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), Guid.NewGuid().ToString());

        // Act & Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(() => 
            repository.AddResourceAsync(resourceInfo, new MemoryStream()));
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowContainerNotFound_WhenContainerWasDeleted()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Act
        await repository.DeleteResourceContainerAsync(containerName);

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(() => 
            repository.AddResourceAsync(resourceInfo, new MemoryStream()));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldDeleteResource_WhenSpecifiedResourceExistsInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "existingResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        await repository.AddResourceAsync(resourceInfo, new MemoryStream());

        // Act
        await repository.DeleteResourceAsync(new ResourceLocation(containerName, resourceInfo.Location.ResourceName));

        // Assert
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldNotDeleteResource_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "existingResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        await repository.AddResourceAsync(resourceInfo, new MemoryStream());

        // Act
        await repository.DeleteResourceAsync(new ResourceLocation(containerName, "nonExistentResource"));

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldNotDeleteResource_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "existingResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        await repository.AddResourceAsync(resourceInfo, new MemoryStream());

        // Act
        await repository.DeleteResourceAsync(new ResourceLocation("nonExistentContainer", "nonExistentName"));

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldReturnAllResources_WhenContainerHoldsResources()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        List<NewResourceVersion> resourceInfos = [
            new NewResourceVersion(new ResourceLocation(containerName, "Resource1"), Guid.NewGuid().ToString()),
            new NewResourceVersion(new ResourceLocation(containerName, "Resource2"), Guid.NewGuid().ToString()),
            new NewResourceVersion(new ResourceLocation(containerName, "Resource3"), Guid.NewGuid().ToString())
        ];

        foreach (NewResourceVersion resourceInfo in resourceInfos)
        {
            await repository.AddResourceAsync(resourceInfo, new MemoryStream());
        }

        // Act
        IEnumerable<ResourceInfo> outputResources = await repository.GetAllResourceInfoAsync(containerName);

        // Assert
        Assert.NotEmpty(outputResources);
        Assert.True(outputResources.Count() == resourceInfos.Count);
    }

    [Fact]
    public async Task GetAllResourcesAsync_ShouldReturnEmpty_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        List<NewResourceVersion> resourceInfos = [
            new NewResourceVersion(new ResourceLocation(containerName, "Resource1"), Guid.NewGuid().ToString()),
            new NewResourceVersion(new ResourceLocation(containerName, "Resource2"), Guid.NewGuid().ToString()),
            new NewResourceVersion(new ResourceLocation(containerName, "Resource3"), Guid.NewGuid().ToString())
        ];

        foreach (NewResourceVersion resourceInfo in resourceInfos)
        {
            await repository.AddResourceAsync(resourceInfo, new MemoryStream());
        }

        // Act
        IEnumerable<ResourceInfo> outputResources = await repository.GetAllResourceInfoAsync("nonExistentContainer");

        // Assert
        Assert.Empty(outputResources);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldGetResource_WhenSpecifiedResourceExistsInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var clock = new ManualTimeProvider();
        var repository = new ResourceInMemoryRepository(clock);
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "existingResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        var stream = new MemoryStream();
        await repository.AddResourceAsync(resourceInfo, stream);

        // Act
        ResourceInfo? outputResource = await repository.GetResourceInfoAsync(new ResourceLocation(containerName, resourceInfo.Location.ResourceName));

        // Assert: stored as current, with the length and hash measured from the (empty) stream and the time it was stored.
        Assert.Equivalent(new ResourceInfo
        {
            ContainerName = containerName,
            Name = resourceInfo.Location.ResourceName,
            VersionId = resourceInfo.VersionId,
            IsLatest = true,
            ContentLength = 0,
            ContentHash = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            LastModified = clock.Now
        }, outputResource);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldNotGetResource_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "existingResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        var stream = new MemoryStream();
        await repository.AddResourceAsync(resourceInfo, stream);

        // Act
        ResourceInfo? outputResource = await repository.GetResourceInfoAsync(new ResourceLocation(containerName, "nonExistentResource"));

        // Assert
        Assert.Null(outputResource);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldNotGetResource_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, "existingResourceName"), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        var stream = new MemoryStream();
        await repository.AddResourceAsync(resourceInfo, stream);

        // Act
        ResourceInfo? outputResource = await repository.GetResourceInfoAsync(new ResourceLocation("nonExistentContainer", resourceInfo.Location.ResourceName));

        // Assert
        Assert.Null(outputResource);
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldGetResourceStream_WhenSpecifiedResourceExistsInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "existingResourceName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, resourceName), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        var stream = new MemoryStream("Test"u8.ToArray());
        await repository.AddResourceAsync(resourceInfo, stream);

        // Act
        Stream? outputStream = await repository.GetResourceStreamAsync(new ResourceLocation(containerName, resourceName));

        // Assert
        Assert.Equal("Test", new StreamReader(outputStream!).ReadToEnd());
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldNotGetResourceStream_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "existingResourceName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, resourceName), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        var stream = new MemoryStream("Test"u8.ToArray());
        await repository.AddResourceAsync(resourceInfo, stream);

        // Act
        Stream? outputStream = await repository.GetResourceStreamAsync(new ResourceLocation(containerName, "nonExistentResource"));

        // Assert
        Assert.Null(outputStream);
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldNotGetResourceStream_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "existingResourceName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new NewResourceVersion(new ResourceLocation(containerName, resourceName), Guid.NewGuid().ToString());
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        var stream = new MemoryStream("Test"u8.ToArray());
        await repository.AddResourceAsync(resourceInfo, stream);

        // Act
        Stream? outputStream = await repository.GetResourceStreamAsync(new ResourceLocation("nonExistentContainer", resourceName));

        // Assert
        Assert.Null(outputStream);
    }

    [Fact]
    public async Task AddResourceContainerAsync_ShouldAddContainer_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();

        // Act
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldDeleteResourceContainer_WhenContainerExists()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Act
        await repository.DeleteResourceContainerAsync(containerName);

        // Assert
        Assert.Empty(await repository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldNotDeleteAnyResourceContainer_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Act
        await repository.DeleteResourceContainerAsync("nonExistentContainer");

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldGetCorrectContainer_WhenGivenExistingName()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Act
        ResourceContainerInfo? outputResourceContainer = await repository.GetResourceContainerInfoAsync(containerName);

        // Assert
        Assert.NotNull(outputResourceContainer);
        Assert.Equal(containerName, outputResourceContainer.Name);
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldNotGetAnyContainer_WhenGivenNonExistingName()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Act
        ResourceContainerInfo? outputResourceContainer = await repository.GetResourceContainerInfoAsync("nonExistingContainer");

        // Assert
        Assert.Null(outputResourceContainer);
    }

    [Fact]
    public async Task GetAllResourceContainerInfoAsync_ShouldReturnAllContainers_WhenMultipleContainersExist()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();

        // Act
        await repository.AddResourceContainerAsync("container1", new Dictionary<string, string>());
        await repository.AddResourceContainerAsync("container2", new Dictionary<string, string>());

        // Assert
        Assert.Equal(2, (await repository.GetAllResourceContainerInfoAsync()).Count);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldAddToRepository_WhenResourceWithSameContainerAndNameExists()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var resourceName = "newResourceName";
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo1 = new NewResourceVersion(new ResourceLocation(containerName, resourceName), date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")) { LastModified = date1 };
        var resourceInfo2 = new NewResourceVersion(new ResourceLocation(containerName, resourceName), date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")) { LastModified = date2 };
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());

        // Act
        await repository.AddResourceAsync(resourceInfo1, new MemoryStream());
        await repository.AddResourceAsync(resourceInfo2, new MemoryStream());

        // Assert
        Assert.Equal(2, (await repository.GetResourceVersionsAsync(new ResourceLocation(containerName, resourceName))).Count);
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldOnlyReturnMostRecentVersion_WhenThereAreMultipleVersions()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var containerName = "containerName";
        var clock = new ManualTimeProvider();
        var repository = new ResourceInMemoryRepository(clock);
        var resourceInfo1 = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        var resourceInfo2 = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        clock.Now = date1;
        await repository.AddResourceAsync(resourceInfo1, new MemoryStream());
        clock.Now = date2;
        await repository.AddResourceAsync(resourceInfo2, new MemoryStream());

        // Act
        IEnumerable<ResourceInfo> resources = await repository.GetAllResourceInfoAsync(containerName);

        // Assert
        var single = Assert.Single(resources);
        Assert.Equal(resourceInfo2.VersionId, single.VersionId);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldOnlyReturnMostRecentVersion_WhenNoVersionIsPassedIn()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var containerName = "containerName";
        var clock = new ManualTimeProvider();
        var repository = new ResourceInMemoryRepository(clock);
        var resourceInfo1 = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        var resourceInfo2 = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        clock.Now = date1;
        await repository.AddResourceAsync(resourceInfo1, new MemoryStream());
        clock.Now = date2;
        await repository.AddResourceAsync(resourceInfo2, new MemoryStream());

        // Act
        var resource = await repository.GetResourceInfoAsync(new ResourceLocation(containerName, "newResourceName"));

        // Assert
        Assert.Equal(date2, resource!.LastModified);
        Assert.Equal(date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), resource!.VersionId);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldReturnRequestedVersion_WhenVersionIsPassedIn()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var containerName = "containerName";
        var clock = new ManualTimeProvider();
        var repository = new ResourceInMemoryRepository(clock);
        var resourceInfo1 = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        var resourceInfo2 = new NewResourceVersion(new ResourceLocation(containerName, "newResourceName"), date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        clock.Now = date1;
        await repository.AddResourceAsync(resourceInfo1, new MemoryStream());
        clock.Now = date2;
        await repository.AddResourceAsync(resourceInfo2, new MemoryStream());

        // Act
        var resource = await repository.GetResourceInfoAsync(new ResourceLocation(containerName, "newResourceName"), date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));

        // Assert
        Assert.Equal(date1, resource!.LastModified);
        Assert.Equal(date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), resource!.VersionId);
    }

    [Fact]
    public async Task GetResourceVersionsAsync_ShouldReturnAllVersions_WhenMultipleVersionsExist()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var resourceName = "resourceName";
        var resourceContainerName = "containerName";
        var resourceInfo1 = new NewResourceVersion(new ResourceLocation(resourceContainerName, resourceName), date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")) { LastModified = date1 };
        var resourceInfo2 = new NewResourceVersion(new ResourceLocation(resourceContainerName, resourceName), date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")) { LastModified = date2 };
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(resourceContainerName, new Dictionary<string, string>());
        var stream = new MemoryStream();
        await repository.AddResourceAsync(resourceInfo1, stream);
        await repository.AddResourceAsync(resourceInfo2, stream);

        // Act
        var resources = await repository.GetResourceVersionsAsync(new ResourceLocation(resourceContainerName, resourceName));

        // Assert
        Assert.Equal(2, resources.Count);
        Assert.Equal(resourceInfo2.VersionId, resources.First().VersionId);
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldMakeItsResourcesUnreadable_WhenContainerHeldResources()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "resourceName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation(containerName, resourceName), Guid.NewGuid().ToString()), new MemoryStream([1, 2, 3]));

        // Act
        await repository.DeleteResourceContainerAsync(containerName);

        // Assert
        Assert.Null(await repository.GetResourceInfoAsync(new ResourceLocation(containerName, resourceName)));
        Assert.Null(await repository.GetResourceStreamAsync(new ResourceLocation(containerName, resourceName)));
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
        Assert.Empty(await repository.GetResourceVersionsAsync(new ResourceLocation(containerName, resourceName)));
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldReturnACopyOfTheStoredMetadata_WhenTheContainerWasAddedWithIt()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        var metadata = new Dictionary<string, string> { ["key"] = "original" };
        await repository.AddResourceContainerAsync("c", metadata);
        metadata["key"] = "changed by the caller";

        // Act
        ResourceContainerInfo? first = await repository.GetResourceContainerInfoAsync("c");
        first!.Metadata["key"] = "changed by a reader";
        ResourceContainerInfo listed = Assert.Single(await repository.GetAllResourceContainerInfoAsync());

        // Assert
        Assert.Equal("original", listed.Metadata["key"]);
        Assert.Equal("original", (await repository.GetResourceContainerInfoAsync("c"))!.Metadata["key"]);
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldReturnAReadOnlyForwardOnlyStream_WhenTheResourceExists()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1"), new MemoryStream([1, 2, 3, 4, 5, 6]));

        // Act
        await using Stream stream = (await repository.GetResourceStreamAsync(new ResourceLocation("c", "n")))!;
        int read = await stream.ReadAsync(new byte[4]);

        // Assert
        Assert.IsNotAssignableFrom<MemoryStream>(stream);
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);
        Assert.Equal(6, stream.Length);
        Assert.Equal(read, stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldThrowOperationCanceledAndDeleteNothing_WhenTokenIsAlreadyCanceled()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(containerName, new Dictionary<string, string>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Task act() => repository.DeleteResourceContainerAsync(containerName, cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.NotNull(await repository.GetResourceContainerInfoAsync(containerName));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldLeaveNoTraceOfTheName_WhenGivenTheIdOfItsOnlyVersion()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1"), new MemoryStream());

        // Act
        bool deleted = await repository.DeleteResourceAsync(new ResourceLocation("c", "n"), "v1");

        // Assert
        Assert.True(deleted);
        Assert.Empty(await repository.GetAllResourceInfoAsync("c"));
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v2"), new MemoryStream(), new ResourceWriteCondition { CreateOnly = true });
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowConflictAndStoreNothing_WhenCreateOnlyAndTheNameExists()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "first"), new MemoryStream());

        // Act
        Task act() => repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "second"), new MemoryStream(), new ResourceWriteCondition { CreateOnly = true });

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal("first", Assert.Single(await repository.GetResourceVersionsAsync(new ResourceLocation("c", "n"))).VersionId);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowConflictAndStoreNothingMore_WhenTheVersionIdIsAlreadyStored()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1"), new MemoryStream([1]));

        // Act
        Task act() => repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1"), new MemoryStream([2]));

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Single(await repository.GetResourceVersionsAsync(new ResourceLocation("c", "n")));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStore_WhenCreateOnlyAndTheNameIsNew()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());

        // Act
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "first"), new MemoryStream(), new ResourceWriteCondition { CreateOnly = true });

        // Assert
        Assert.NotNull(await repository.GetResourceInfoAsync(new ResourceLocation("c", "n")));
    }

    [Fact]
    public async Task AddResourceContainerAsync_ShouldThrowConflict_WhenTheContainerExists()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());

        // Act
        Task act() => repository.AddResourceContainerAsync("c", new Dictionary<string, string>());

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Single(await repository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStoreAndReturnTheMeasuredLengthAndHash_WhenNoLengthIsDeclared()
    {
        // Arrange
        byte[] payload = Encoding.UTF8.GetBytes("measured, not declared");
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());

        // Act
        WrittenContent written = await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1"), new MemoryStream(payload));

        // Assert
        Assert.Equal(payload.Length, written.ContentLength);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload)), written.ContentHash);
        Assert.Equal(payload.Length, (await repository.GetResourceInfoAsync(new ResourceLocation("c", "n")))!.ContentLength);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowAndStoreNothing_WhenTheDeclaredLengthIsWrong()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());

        // Act
        Task act() => repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1") { DeclaredLength = 4 }, new MemoryStream([1, 2, 3]));

        // Assert
        await Assert.ThrowsAsync<ResourceContentLengthMismatchException>(act);
        Assert.Null(await repository.GetResourceInfoAsync(new ResourceLocation("c", "n")));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldDeleteEveryVersion_WhenNoVersionIsGiven()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "first"), new MemoryStream());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "second"), new MemoryStream());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "other"), "kept"), new MemoryStream());

        // Act
        bool deleted = await repository.DeleteResourceAsync(new ResourceLocation("c", "n"));

        // Assert
        Assert.True(deleted);
        Assert.Empty(await repository.GetResourceVersionsAsync(new ResourceLocation("c", "n")));
        Assert.Null(await repository.GetResourceInfoAsync(new ResourceLocation("c", "n")));
        Assert.Equal("other", Assert.Single(await repository.GetAllResourceInfoAsync("c")).Name);
    }

    /// <summary>Runs <paramref name="action"/> just before the first read of <paramref name="content"/>.</summary>
    private sealed class BeforeFirstReadStream(byte[] content, Func<Task> action) : MemoryStream(content)
    {
        private bool _done;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await action();
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldGiveEveryReaderTheWholeContent_WhenReadInParallel()
    {
        // Arrange
        byte[] payload = RandomNumberGenerator.GetBytes(100_000);
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1"), new MemoryStream(payload));

        // Act
        byte[][] reads = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await using Stream stream = (await repository.GetResourceStreamAsync(new ResourceLocation("c", "n")))!;
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        })));

        // Assert
        Assert.All(reads, read => Assert.Equal(payload, read));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStoreEveryResource_WhenUploadsAndListingsRunInParallel()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());

        // Act
        Task[] work = [.. Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            await repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", $"n{i}"), "v1"), new MemoryStream([1]));
            await repository.GetAllResourceInfoAsync("c");
        }))];
        await Task.WhenAll(work);

        // Assert
        Assert.Equal(64, (await repository.GetAllResourceInfoAsync("c")).Count);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldLetExactlyOneSucceed_WhenConcurrentCreateOnlyWritesTargetOneName()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());

        // Act
        Exception?[] outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => Record.ExceptionAsync(() =>
            repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), $"v{i}"), new MemoryStream([1]), new ResourceWriteCondition { CreateOnly = true })))));

        // Assert
        Assert.Single(outcomes, o => o is null);
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ResourceConflictException>(o));
        Assert.Single(await repository.GetResourceVersionsAsync(new ResourceLocation("c", "n")));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowAndStoreNothing_WhenTheContainerIsDeletedWhileTheContentIsRead()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        var content = new BeforeFirstReadStream([1, 2, 3],() => repository.DeleteResourceContainerAsync("c"));

        // Act
        Task act() => repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation("c", "n"), "v1"), content);

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(act);
        await repository.AddResourceContainerAsync("c", new Dictionary<string, string>());
        Assert.Null(await repository.GetResourceInfoAsync(new ResourceLocation("c", "n")));
    }
}
