using System.Security.Cryptography;
using System.Text;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit.InMemory;

public class ResourceInMemoryRepositoryTests
{
    [Fact]
    public async Task AddResourceAsync_ShouldAddToRepository_WhenContainerExists()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "newResourceName" };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);

        // Act
        await repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowContainerNotFound_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "newResourceName" };

        // Act & Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(() => 
            repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream()));
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowContainerNotFound_WhenContainerWasDeleted()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "newResourceName" };
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });

        // Act
        await repository.DeleteResourceContainerAsync(containerName);

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(() => 
            repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream()));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldDeleteResource_WhenSpecifiedResourceExistsInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = "ContainerName", Name = "existingResourceName" };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        await repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());

        // Act
        await repository.DeleteResourceAsync(containerName, resourceInfo.Name);

        // Assert
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldNotDeleteResource_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = "ContainerName", Name = "existingResourceName" };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        await repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());

        // Act
        await repository.DeleteResourceAsync(containerName, "nonExistentResource");

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldNotDeleteResource_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = "ContainerName", Name = "existingResourceName" };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        await repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());

        // Act
        await repository.DeleteResourceAsync("nonExistentContainer", "nonExistentName");

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldReturnAllResources_WhenContainerHoldsResources()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        List<ResourceInfo> resourceInfos = [
            new ResourceInfo { ContainerName = "ContainerName", Name = "Resource1" },
            new ResourceInfo { ContainerName = "ContainerName", Name = "Resource2" },
            new ResourceInfo { ContainerName = "ContainerName", Name = "Resource3" }
        ];

        foreach (ResourceInfo resourceInfo in resourceInfos)
        {
            await repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());
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
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        List<ResourceInfo> resourceInfos = [
            new ResourceInfo { ContainerName = "ContainerName", Name = "Resource1" },
            new ResourceInfo { ContainerName = "ContainerName", Name = "Resource2" },
            new ResourceInfo { ContainerName = "ContainerName", Name = "Resource3" }
        ];

        foreach (ResourceInfo resourceInfo in resourceInfos)
        {
            await repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());
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
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "existingResourceName" };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        var stream = new MemoryStream();
        await repository.AddResourceAsync(containerName, resourceInfo, stream);

        // Act
        ResourceInfo? outputResource = await repository.GetResourceInfoAsync(containerName, resourceInfo.Name);

        // Assert: stored as current, with the length measured from the (empty) stream.
        resourceInfo.IsLatest = true;
        resourceInfo.ContentLength = 0;
        Assert.Equivalent(resourceInfo, outputResource);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldNotGetResource_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "existingResourceName" };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        var stream = new MemoryStream();
        await repository.AddResourceAsync(containerName, resourceInfo, stream);

        // Act
        ResourceInfo? outputResource = await repository.GetResourceInfoAsync(containerName, "nonExistentResource");

        // Assert
        Assert.Null(outputResource);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldNotGetResource_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "existingResourceName" };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        var stream = new MemoryStream();
        await repository.AddResourceAsync(containerName, resourceInfo, stream);

        // Act
        ResourceInfo? outputResource = await repository.GetResourceInfoAsync("nonExistentContainer", resourceInfo.Name);

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
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = resourceName };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write("Test");
        stream.Position = 0;
        await repository.AddResourceAsync(containerName, resourceInfo, stream);

        // Act
        Stream? outputStream = await repository.GetResourceStreamAsync(containerName, resourceName);

        // Assert
        var readerOriginal = new StreamReader(stream);
        var readerOutput = new StreamReader(outputStream!);
        var originalContent = readerOriginal.ReadToEnd();
        var outputContent = readerOutput.ReadToEnd();
        Assert.Equal(originalContent, outputContent);
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldNotGetResourceStream_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "existingResourceName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = resourceName };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write("Test");
        stream.Position = 0;
        await repository.AddResourceAsync(containerName, resourceInfo, stream);

        // Act
        Stream? outputStream = await repository.GetResourceStreamAsync(containerName, "nonExistentResource");

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
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = resourceName };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write("Test");
        stream.Position = 0;
        await repository.AddResourceAsync(containerName, resourceInfo, stream);

        // Act
        Stream? outputStream = await repository.GetResourceStreamAsync("nonExistentContainer", resourceName);

        // Assert
        Assert.Null(outputStream);
    }

    [Fact]
    public async Task AddResourceContainerAsync_ShouldAddContainer_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };

        // Act
        await repository.AddResourceContainerAsync(resourceContainerInfo);

        // Assert
        Assert.NotEmpty(await repository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldDeleteResourceContainer_WhenContainerExists()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);

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
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);

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
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);

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
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);

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
        var resourceContainerInfo1 = new ResourceContainerInfo() { Name = "container1" };
        var resourceContainerInfo2 = new ResourceContainerInfo() { Name = "container2" };

        // Act
        await repository.AddResourceContainerAsync(resourceContainerInfo1);
        await repository.AddResourceContainerAsync(resourceContainerInfo2);

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
        var resourceInfo1 = new ResourceInfo { ContainerName = containerName, Name = resourceName, VersionId = date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date1 };
        var resourceInfo2 = new ResourceInfo { ContainerName = containerName, Name = resourceName, VersionId = date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date2 };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);

        // Act
        await repository.AddResourceAsync(containerName, resourceInfo1, new MemoryStream());
        await repository.AddResourceAsync(containerName, resourceInfo2, new MemoryStream());

        // Assert
        Assert.Equal(2, (await repository.GetResourceVersionsAsync(containerName, resourceName)).Count);
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldOnlyReturnMostRecentVersion_WhenThereAreMultipleVersions()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        var resourceInfo1 = new ResourceInfo { ContainerName = containerName, Name = "newResourceName", VersionId = date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date1 };
        var resourceInfo2 = new ResourceInfo { ContainerName = containerName, Name = "newResourceName", VersionId = date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date2 };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        await repository.AddResourceAsync(containerName, resourceInfo1, new MemoryStream());
        await repository.AddResourceAsync(containerName, resourceInfo2, new MemoryStream());

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
        var repository = new ResourceInMemoryRepository();
        var resourceInfo1 = new ResourceInfo { ContainerName = containerName, Name = "newResourceName", VersionId = date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date1 };
        var resourceInfo2 = new ResourceInfo { ContainerName = containerName, Name = "newResourceName", VersionId = date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date2 };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        await repository.AddResourceAsync(containerName, resourceInfo1, new MemoryStream());
        await repository.AddResourceAsync(containerName, resourceInfo2, new MemoryStream());

        // Act
        var resource = await repository.GetResourceInfoAsync(containerName, "newResourceName");

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
        var repository = new ResourceInMemoryRepository();
        var resourceInfo1 = new ResourceInfo { ContainerName = containerName, Name = "newResourceName", VersionId = date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date1 };
        var resourceInfo2 = new ResourceInfo { ContainerName = containerName, Name = "newResourceName", VersionId = date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), LastModified = date2 };
        var resourceContainerInfo = new ResourceContainerInfo() { Name = containerName };
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        await repository.AddResourceAsync(containerName, resourceInfo1, new MemoryStream());
        await repository.AddResourceAsync(containerName, resourceInfo2, new MemoryStream());

        // Act
        var resource = await repository.GetResourceInfoAsync(containerName, "newResourceName", date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));

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
        var resourceInfo1 = new ResourceInfo
        {
            ContainerName = resourceContainerName,
            Name = resourceName,
            VersionId = date1.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            LastModified = date1
        };
        var resourceInfo2 = new ResourceInfo
        {
            ContainerName = resourceContainerName,
            Name = resourceName,
            VersionId = date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
            LastModified = date2
        };
        var resourceContainerInfo = new ResourceContainerInfo { Name = resourceContainerName };
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(resourceContainerInfo);
        var stream = new MemoryStream();
        await repository.AddResourceAsync(resourceContainerName, resourceInfo1, stream);
        await repository.AddResourceAsync(resourceContainerName, resourceInfo2, stream);

        // Act
        var resources = await repository.GetResourceVersionsAsync(resourceContainerName, resourceName);

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
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        await repository.AddResourceAsync(containerName, new ResourceInfo { ContainerName = containerName, Name = resourceName }, new MemoryStream([1, 2, 3]));

        // Act
        await repository.DeleteResourceContainerAsync(containerName);

        // Assert
        Assert.Null(await repository.GetResourceInfoAsync(containerName, resourceName));
        Assert.Null(await repository.GetResourceStreamAsync(containerName, resourceName));
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
        Assert.Empty(await repository.GetResourceVersionsAsync(containerName, resourceName));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStoreACopyOfMetadata_WhenCallerMutatesTheDictionaryAfterwards()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "resourceName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        var metadata = new Dictionary<string, string> { ["key"] = "original" };
        await repository.AddResourceAsync(containerName, new ResourceInfo { ContainerName = containerName, Name = resourceName, Metadata = metadata }, new MemoryStream());

        // Act
        metadata["key"] = "changed";
        metadata["extra"] = "added";

        // Assert
        ResourceInfo? stored = await repository.GetResourceInfoAsync(containerName, resourceName);
        Assert.Equal("original", stored!.Metadata["key"]);
        Assert.False(stored.Metadata.ContainsKey("extra"));
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldReturnACopyOfMetadata_WhenCallerMutatesTheReturnedDictionary()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "resourceName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        await repository.AddResourceAsync(containerName, new ResourceInfo { ContainerName = containerName, Name = resourceName, Metadata = new Dictionary<string, string> { ["key"] = "original" } }, new MemoryStream());
        ResourceInfo? first = await repository.GetResourceInfoAsync(containerName, resourceName);

        // Act
        first!.Metadata["key"] = "changed";

        // Assert
        ResourceInfo? second = await repository.GetResourceInfoAsync(containerName, resourceName);
        Assert.Equal("original", second!.Metadata["key"]);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowOperationCanceledAndStoreNothing_WhenTokenIsAlreadyCancelled()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "resourceName" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Task act() => repository.AddResourceAsync(containerName, resourceInfo, new MemoryStream([1, 2, 3]), cancellationToken: cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowOperationCanceledAndStoreNothing_WhenTokenIsCancelledMidStream()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        var resourceInfo = new ResourceInfo { ContainerName = containerName, Name = "resourceName" };
        using var cts = new CancellationTokenSource();
        var content = new CancelAfterReadsStream(new byte[200_000], cts, readsBeforeCancel: 1);

        // Act
        Task act() => repository.AddResourceAsync(containerName, resourceInfo, content, cancellationToken: cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Empty(await repository.GetAllResourceInfoAsync(containerName));
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldThrowOperationCanceled_WhenTokenIsAlreadyCancelled()
    {
        // Arrange & Act
        var repository = new ResourceInMemoryRepository();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetAllResourceInfoAsync("containerName", cts.Token));
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldThrowOperationCanceled_WhenTokenIsAlreadyCancelled()
    {
        // Arrange & Act
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        await repository.AddResourceAsync(containerName, new ResourceInfo { ContainerName = containerName, Name = "resourceName" }, new MemoryStream([1, 2, 3]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetResourceStreamAsync(containerName, "resourceName", null, cts.Token));
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldThrowOperationCanceledAndDeleteNothing_WhenTokenIsAlreadyCancelled()
    {
        // Arrange
        var containerName = "containerName";
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Task act() => repository.DeleteResourceContainerAsync(containerName, cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.NotNull(await repository.GetResourceContainerInfoAsync(containerName));
    }

    [Fact]
    public async Task GetResourceVersionsAsync_ShouldPutTheLastStoredVersionFirst_WhenItsClockIsBehind()
    {
        // Arrange: the second writer's clock is an hour behind the first's.
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "ahead", LastModified = DateTime.UtcNow.AddHours(1) }, new MemoryStream());
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "behind", LastModified = DateTime.UtcNow }, new MemoryStream());

        // Act
        List<ResourceInfo> versions = await repository.GetResourceVersionsAsync("c", "n");
        ResourceInfo? current = await repository.GetResourceInfoAsync("c", "n");

        // Assert
        Assert.Equal(["behind", "ahead"], versions.Select(v => v.VersionId));
        Assert.True(versions[0].IsLatest);
        Assert.Equal("behind", current!.VersionId);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldPromoteTheLastStoredRemainingVersion_WhenAnEarlierOneHasALaterClock()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "first", LastModified = DateTime.UtcNow.AddHours(1) }, new MemoryStream());
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "second", LastModified = DateTime.UtcNow }, new MemoryStream());
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "third", LastModified = DateTime.UtcNow }, new MemoryStream());

        // Act
        await repository.DeleteResourceAsync("c", "n", "third");

        // Assert
        Assert.Equal("second", (await repository.GetResourceInfoAsync("c", "n"))!.VersionId);
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldReturnTheLastStoredVersion_WhenItsClockIsBehind()
    {
        // Arrange: the second writer's clock is an hour behind the first's.
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "ahead", LastModified = DateTime.UtcNow.AddHours(1) }, new MemoryStream([1]));
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "behind", LastModified = DateTime.UtcNow }, new MemoryStream([2]));

        // Act
        List<ResourceInfo> listed = await repository.GetAllResourceInfoAsync("c");
        Stream? current = await repository.GetResourceStreamAsync("c", "n");

        // Assert
        Assert.Equal("behind", Assert.Single(listed).VersionId);
        Assert.Equal(2, current!.ReadByte());
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowConflictAndStoreNothing_WhenCreateOnlyAndTheNameExists()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "first" }, new MemoryStream());

        // Act
        Task act() => repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "second" }, new MemoryStream(), createOnly: true);

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal("first", Assert.Single(await repository.GetResourceVersionsAsync("c", "n")).VersionId);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStore_WhenCreateOnlyAndTheNameIsNew()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });

        // Act
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "first" }, new MemoryStream(), createOnly: true);

        // Assert
        Assert.NotNull(await repository.GetResourceInfoAsync("c", "n"));
    }

    [Fact]
    public async Task AddResourceContainerAsync_ShouldThrowConflict_WhenTheContainerExists()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });

        // Act
        Task act() => repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });

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
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });

        // Act
        WrittenContent written = await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "v1" }, new MemoryStream(payload));

        // Assert
        Assert.Equal(payload.Length, written.ContentLength);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload)), written.ContentHash);
        Assert.Equal(payload.Length, (await repository.GetResourceInfoAsync("c", "n"))!.ContentLength);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowAndStoreNothing_WhenTheDeclaredLengthIsWrong()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });

        // Act
        Task act() => repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "v1", ContentLength = 4 }, new MemoryStream([1, 2, 3]));

        // Assert
        await Assert.ThrowsAsync<ResourceContentLengthMismatchException>(act);
        Assert.Null(await repository.GetResourceInfoAsync("c", "n"));
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldDeleteEveryVersion_WhenNoVersionIsGiven()
    {
        // Arrange
        var repository = new ResourceInMemoryRepository();
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = "c" });
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "first" }, new MemoryStream());
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "second" }, new MemoryStream());
        await repository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "other", VersionId = "kept" }, new MemoryStream());

        // Act
        bool deleted = await repository.DeleteResourceAsync("c", "n");

        // Assert
        Assert.True(deleted);
        Assert.Empty(await repository.GetResourceVersionsAsync("c", "n"));
        Assert.Null(await repository.GetResourceInfoAsync("c", "n"));
        Assert.Equal("other", Assert.Single(await repository.GetAllResourceInfoAsync("c")).Name);
    }
}
