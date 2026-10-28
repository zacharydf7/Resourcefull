using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Models;
using System.Text;

namespace Resourcefull.Tests.Unit.SqlServer;
public class ResourceSqlRepositoryTests
{
    private static (ResourceDbContext context, PooledDbContextFactory<ResourceDbContext> factory) NewDatabase()
    {
        // The in-memory provider has no transactions; the writes' transactional behavior is covered by the
        // SQL Server integration tests.
        DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return (new ResourceDbContext(options), new PooledDbContextFactory<ResourceDbContext>(options));
    }

    private static ResourceInfo Version(string container, string name, DateTime when) => new()
    {
        ContainerName = container,
        Name = name,
        VersionId = when.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
        LastModified = when
    };

    [Fact]
    public async Task AddResourceAsync_ShouldAddToRepository_WhenContainerExists()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var resourceInfo = new ResourceInfo { ContainerName = resourceContainerName, Name = "newResourceName" };
        var resourceContainerInfo = new ResourceContainerModel { Name = resourceContainerName };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(resourceContainerInfo);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo, new MemoryStream());

        // Assert
        Assert.NotEmpty(resourceDbContext.Resources);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldMarkNewResourceAsLatest_WhenResourceIsAdded()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var resourceInfo = new ResourceInfo { ContainerName = resourceContainerName, Name = "newResourceName" };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = resourceContainerName });
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo, new MemoryStream());

        // Assert
        Assert.True(resourceDbContext.Resources.Single().IsLatest);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldDeleteResource_WhenSpecifiedResourceExistsInContainer()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var existingResource = new ResourceModel { ContainerName = resourceContainerName, Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();
        var resourceName = resourceDbContext.Resources.First().Name;

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.DeleteResourceAsync(resourceContainerName, resourceName);

        // Assert
        Assert.Empty(resourceDbContext.Resources);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldNotDeleteResource_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var existingResource = new ResourceModel { ContainerName = resourceContainerName, Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.DeleteResourceAsync(resourceContainerName, "nonExistentResource");

        // Assert
        Assert.NotEmpty(resourceDbContext.Resources);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldNotDeleteResource_WhenSpecifiedContainerDoesNotExist()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var existingResource = new ResourceModel { ContainerName = resourceContainerName, Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();
        var resourceName = resourceDbContext.Resources.First().Name;

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.DeleteResourceAsync("nonExistentContainer", resourceName);

        // Assert
        Assert.NotEmpty(resourceDbContext.Resources);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldPromoteNextVersionToLatest_WhenLatestVersionIsDeleted()
    {
        // Arrange
        var container = "containerName";
        var name = "resourceName";
        var older = Version(container, name, DateTime.UtcNow.AddMinutes(-1));
        var newer = Version(container, name, DateTime.UtcNow);
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = container });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(container, older, new MemoryStream());
        await resourceRepository.AddResourceAsync(container, newer, new MemoryStream());

        // Act - delete the current (latest) version
        await resourceRepository.DeleteResourceAsync(container, name);

        // Assert - the older version is now resolvable as latest
        var latest = await resourceRepository.GetResourceInfoAsync(container, name);
        Assert.NotNull(latest);
        Assert.Equal(older.VersionId, latest!.VersionId);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldDeleteSpecificVersion_WhenVersionIdProvided()
    {
        // Arrange
        var container = "containerName";
        var name = "resourceName";
        var older = Version(container, name, DateTime.UtcNow.AddMinutes(-1));
        var newer = Version(container, name, DateTime.UtcNow);
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = container });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(container, older, new MemoryStream());
        await resourceRepository.AddResourceAsync(container, newer, new MemoryStream());

        // Act - delete the specific (older, non-latest) version
        await resourceRepository.DeleteResourceAsync(container, name, older.VersionId);

        // Assert - only the newer version remains and is still latest
        var versions = await resourceRepository.GetResourceVersionsAsync(container, name);
        Assert.Single(versions);
        Assert.Equal(newer.VersionId, versions.Single().VersionId);
        Assert.Equal(newer.VersionId, (await resourceRepository.GetResourceInfoAsync(container, name))!.VersionId);
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldReturnAllResources_WhenContainerHoldsResources()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var existingResource = new ResourceModel { ContainerName = resourceContainerName, Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        IEnumerable<ResourceInfo> outputResources = await resourceRepository.GetAllResourceInfoAsync(resourceContainerName);

        // Assert
        Assert.NotEmpty(outputResources);
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldReturnEmpty_WhenContainerDoesNotExist()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var existingResource = new ResourceModel { ContainerName = resourceContainerName, Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        IEnumerable<ResourceInfo> outputResources = await resourceRepository.GetAllResourceInfoAsync("nonExistentContainer");

        // Assert
        Assert.Empty(outputResources);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldGetResource_WhenSpecifiedResourceExistsInContainer()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var existingResource = new ResourceModel { ContainerName = "containerName", Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();
        var existingResourceName = resourceDbContext.Resources.First().Name;

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        ResourceInfo? outputResource = await resourceRepository.GetResourceInfoAsync(resourceContainerName, existingResourceName);

        // Assert
        Assert.Equal(existingResource.ContainerName, outputResource?.ContainerName);
        Assert.Equal(existingResource.Name, outputResource?.Name);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldNotGetResource_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var resourceContainerName = "containerName";
        var existingResource = new ResourceModel { ContainerName = "containerName", Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        ResourceInfo? outputResource = await resourceRepository.GetResourceInfoAsync(resourceContainerName, "nonExistentResource");

        // Assert
        Assert.Null(outputResource);
    }

    [Fact]
    public async Task GetResourceByNameAsync_ShouldNotGetResource_WhenSpecifiedContainerDoesNotExist()
    {
        // Arrange
        var existingResource = new ResourceModel { ContainerName = "containerName", Name = "existingResourceName", IsLatest = true, Chunks = [] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();
        var existingResourceName = resourceDbContext.Resources.First().Name;

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        ResourceInfo? outputResource = await resourceRepository.GetResourceInfoAsync("nonExistentContainer", existingResourceName);

        // Assert
        Assert.Null(outputResource);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldReturnRequestedVersion_WhenVersionIdProvided()
    {
        // Arrange
        var container = "containerName";
        var name = "resourceName";
        var older = Version(container, name, DateTime.UtcNow.AddMinutes(-1));
        var newer = Version(container, name, DateTime.UtcNow);
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = container });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(container, older, new MemoryStream());
        await resourceRepository.AddResourceAsync(container, newer, new MemoryStream());

        // Act
        ResourceInfo? result = await resourceRepository.GetResourceInfoAsync(container, name, older.VersionId);

        // Assert
        Assert.Equal(older.VersionId, result!.VersionId);
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldGetResourceStream_WhenSpecifiedResourceExistsInContainer()
    {
        // Arrange
        var existingContainerName = "existingContainerName";
        var resourceName = "existingResourceName";
        var bytes = Encoding.UTF8.GetBytes("Hello, World!");
        var existingResource = new ResourceModel { ContainerName = existingContainerName, Name = resourceName, IsLatest = true, Chunks = [new ResourceChunkModel { Index = 1, Data = bytes }] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();
        var existingResourceName = resourceDbContext.Resources.First().Name;

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        Stream? outputStream = await resourceRepository.GetResourceStreamAsync(existingContainerName, existingResourceName);

        // Assert
        var readerOutput = new StreamReader(outputStream!);
        var outputContent = readerOutput.ReadToEnd();
        Assert.Equal("Hello, World!", outputContent);
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldReassembleChunksInIndexOrder_WhenChunksAreStoredOutOfOrder()
    {
        // Arrange
        var container = "containerName";
        var resourceName = "resourceName";
        var existingResource = new ResourceModel
        {
            ContainerName = container,
            Name = resourceName,
            IsLatest = true,
            Chunks =
            [
                new ResourceChunkModel { Index = 2, Data = Encoding.UTF8.GetBytes("World!") },
                new ResourceChunkModel { Index = 1, Data = Encoding.UTF8.GetBytes("Hello, ") }
            ]
        };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        Stream? outputStream = await resourceRepository.GetResourceStreamAsync(container, resourceName);

        // Assert
        Assert.Equal("Hello, World!", new StreamReader(outputStream!).ReadToEnd());
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldNotGetResourceStream_WhenSpecifiedResourceDoesNotExistInContainer()
    {
        // Arrange
        var existingContainerName = "existingContainerName";
        var resourceName = "existingResourceName";
        var bytes = Encoding.UTF8.GetBytes("Hello, World!");
        var existingResource = new ResourceModel { ContainerName = existingContainerName, Name = resourceName, IsLatest = true, Chunks = [new ResourceChunkModel { Index = 1, Data = bytes }] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        Stream? outputStream = await resourceRepository.GetResourceStreamAsync(existingContainerName, "nonExistentResource");

        // Assert
        Assert.Null(outputStream);
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldNotGetResourceStream_WhenSpecifiedContainerDoesNotExist()
    {
        // Arrange
        var existingContainerName = "existingContainerName";
        var resourceName = "existingResourceName";
        var bytes = Encoding.UTF8.GetBytes("Hello, World!");
        var existingResource = new ResourceModel { ContainerName = existingContainerName, Name = resourceName, IsLatest = true, Chunks = [new ResourceChunkModel { Index = 1, Data = bytes }] };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.Resources.AddAsync(existingResource);
        await resourceDbContext.SaveChangesAsync();
        var existingResourceName = resourceDbContext.Resources.First().Name;

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        Stream? outputStream = await resourceRepository.GetResourceStreamAsync("nonExistentContainer", existingResourceName);

        // Assert
        Assert.Null(outputStream);
    }

    [Fact]
    public async Task AddResourceContainerAsync_ShouldAddContainer_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceContainerInfo = new ResourceContainerInfo { Name = containerName };

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(resourceContainerInfo);

        // Assert
        Assert.NotEmpty(await resourceRepository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldDeleteResourceContainer_WhenContainerExists()
    {
        // Arrange
        var containerName = "containerName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceContainerInfo = new ResourceContainerInfo { Name = containerName };
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(resourceContainerInfo);

        // Act
        await resourceRepository.DeleteResourceContainerAsync(containerName);

        // Assert
        Assert.Empty(await resourceRepository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldDeleteContainerAndAllItsResources_WhenContainerHoldsResources()
    {
        // Arrange
        var container = "containerName";
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = container });
        var now = DateTime.UtcNow;
        await resourceRepository.AddResourceAsync(container, Version(container, "a", now), new MemoryStream());
        await resourceRepository.AddResourceAsync(container, Version(container, "b", now), new MemoryStream());
        await resourceRepository.AddResourceAsync(container, Version(container, "c", now), new MemoryStream());

        // Act
        var result = await resourceRepository.DeleteResourceContainerAsync(container);

        // Assert
        Assert.True(result);
        Assert.Empty(await resourceRepository.GetAllResourceContainerInfoAsync());
        Assert.Empty(resourceDbContext.Resources.Where(r => r.ContainerName == container));
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldNotDeleteAnyResourceContainer_WhenContainerDoesNotExist()
    {
        // Arrange
        var containerName = "containerName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceContainerInfo = new ResourceContainerInfo { Name = containerName };
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(resourceContainerInfo);

        // Act
        var result = await resourceRepository.DeleteResourceContainerAsync("nonExistentContainer");

        // Assert
        Assert.False(result);
        Assert.NotEmpty(await resourceRepository.GetAllResourceContainerInfoAsync());
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldGetCorrectContainer_WhenGivenExistingName()
    {
        // Arrange
        var containerName = "containerName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceContainerInfo = new ResourceContainerInfo { Name = containerName };
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(resourceContainerInfo);

        // Act
        ResourceContainerInfo? outputResourceContainer = await resourceRepository.GetResourceContainerInfoAsync(containerName);

        // Assert
        Assert.Equal(containerName, outputResourceContainer!.Name);
    }

    [Fact]
    public async Task GetResourceContainerInfoAsync_ShouldNotGetAnyContainer_WhenGivenNonExistingName()
    {
        // Arrange
        var containerName = "containerName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceContainerInfo = new ResourceContainerInfo { Name = containerName };
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(resourceContainerInfo);

        // Act
        ResourceContainerInfo? outputResourceContainer = await resourceRepository.GetResourceContainerInfoAsync("nonExistingContainer");

        // Assert
        Assert.Null(outputResourceContainer);
    }

    [Fact]
    public async Task GetAllResourceContainerInfoAsync_ShouldReturnAllContainers_WhenMultipleContainersExist()
    {
        // Arrange
        (_, var dbContextFactory) = NewDatabase();
        var resourceContainerInfo1 = new ResourceContainerInfo { Name = "container1" };
        var resourceContainerInfo2 = new ResourceContainerInfo { Name = "container2" };
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);

        // Act
        await resourceRepository.AddResourceContainerAsync(resourceContainerInfo1);
        await resourceRepository.AddResourceContainerAsync(resourceContainerInfo2);

        // Assert
        Assert.Equal(2, (await resourceRepository.GetAllResourceContainerInfoAsync()).Count);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldAddToRepository_WhenResourceWithSameContainerAndNameExists()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var resourceContainerName = "containerName";
        var resourceInfo1 = Version(resourceContainerName, "newResourceName", date1);
        var resourceInfo2 = Version(resourceContainerName, "newResourceName", date2);
        var resourceContainerInfo = new ResourceContainerModel { Name = resourceContainerName };
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(resourceContainerInfo);
        await resourceDbContext.SaveChangesAsync();

        // Act
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo1, new MemoryStream());
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo2, new MemoryStream());

        // Assert
        Assert.Equal(2, resourceDbContext.Resources.Count());
    }

    [Fact]
    public async Task AddResourceAsync_ShouldKeepExactlyOneLatest_WhenMultipleVersionsExist()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var resourceContainerName = "containerName";
        var resourceInfo1 = Version(resourceContainerName, "newResourceName", date1);
        var resourceInfo2 = Version(resourceContainerName, "newResourceName", date2);
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = resourceContainerName });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);

        // Act
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo1, new MemoryStream());
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo2, new MemoryStream());

        // Assert
        var latest = resourceDbContext.Resources.Where(r => r.IsLatest).ToList();
        Assert.Single(latest);
        Assert.Equal(resourceInfo2.VersionId, latest.Single().VersionId);
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldOnlyReturnMostRecentVersion_WhenThereAreMultipleVersions()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var resourceContainerName = "containerName";
        var resourceInfo1 = Version(resourceContainerName, "newResourceName", date1);
        var resourceInfo2 = Version(resourceContainerName, "newResourceName", date2);
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = resourceContainerName });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo1, new MemoryStream());
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo2, new MemoryStream());

        // Act
        var resources = await resourceRepository.GetAllResourceInfoAsync(resourceContainerName);

        // Assert
        Assert.Single(resources);
        Assert.Equal(resourceInfo2.VersionId, resources.Single().VersionId);
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldOnlyReturnMostRecentVersion_WhenNoVersionIsPassedIn()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var resourceContainerName = "containerName";
        var resourceInfo1 = Version(resourceContainerName, "newResourceName", date1);
        var resourceInfo2 = Version(resourceContainerName, "newResourceName", date2);
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = resourceContainerName });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo1, new MemoryStream());
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo2, new MemoryStream());

        // Act
        var resource = await resourceRepository.GetResourceInfoAsync(resourceContainerName, "newResourceName");

        // Assert
        Assert.Equal(date2, resource!.LastModified);
        Assert.Equal(date2.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"), resource!.VersionId);
    }

    [Fact]
    public async Task GetResourceVersionsAsync_ShouldReturnAllVersions_WhenMultipleVersionsExist()
    {
        // Arrange
        var date1 = DateTime.UtcNow;
        await Task.Delay(10);
        var date2 = DateTime.UtcNow;
        var resourceContainerName = "containerName";
        var resourceInfo1 = Version(resourceContainerName, "newResourceName", date1);
        var resourceInfo2 = Version(resourceContainerName, "newResourceName", date2);
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = resourceContainerName });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo1, new MemoryStream());
        await resourceRepository.AddResourceAsync(resourceContainerName, resourceInfo2, new MemoryStream());

        // Act
        var resources = await resourceRepository.GetResourceVersionsAsync(resourceContainerName, "newResourceName");

        // Assert
        Assert.Equal(2, resources.Count());
        Assert.Equal(resourceInfo2.VersionId, resources.First().VersionId);
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldMakeItsResourcesUnreadable_WhenContainerHeldResources()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "resourceName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        await resourceRepository.AddResourceAsync(containerName, Version(containerName, resourceName, DateTime.UtcNow), new MemoryStream([1, 2, 3]));

        // Act
        await resourceRepository.DeleteResourceContainerAsync(containerName);

        // Assert
        Assert.Null(await resourceRepository.GetResourceInfoAsync(containerName, resourceName));
        Assert.Null(await resourceRepository.GetResourceStreamAsync(containerName, resourceName));
        Assert.Empty(await resourceRepository.GetAllResourceInfoAsync(containerName));
        Assert.Empty(await resourceRepository.GetResourceVersionsAsync(containerName, resourceName));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStoreACopyOfMetadata_WhenCallerMutatesTheDictionaryAfterwards()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "resourceName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        var metadata = new Dictionary<string, string> { ["key"] = "original" };
        var resourceInfo = Version(containerName, resourceName, DateTime.UtcNow);
        resourceInfo.Metadata = metadata;
        await resourceRepository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());

        // Act
        metadata["key"] = "changed";
        metadata["extra"] = "added";

        // Assert
        ResourceInfo? stored = await resourceRepository.GetResourceInfoAsync(containerName, resourceName);
        Assert.Equal("original", stored!.Metadata["key"]);
        Assert.False(stored.Metadata.ContainsKey("extra"));
    }

    [Fact]
    public async Task GetResourceInfoAsync_ShouldReturnACopyOfMetadata_WhenCallerMutatesTheReturnedDictionary()
    {
        // Arrange
        var containerName = "containerName";
        var resourceName = "resourceName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        var resourceInfo = Version(containerName, resourceName, DateTime.UtcNow);
        resourceInfo.Metadata = new Dictionary<string, string> { ["key"] = "original" };
        await resourceRepository.AddResourceAsync(containerName, resourceInfo, new MemoryStream());
        ResourceInfo? first = await resourceRepository.GetResourceInfoAsync(containerName, resourceName);

        // Act
        first!.Metadata["key"] = "changed";

        // Assert
        ResourceInfo? second = await resourceRepository.GetResourceInfoAsync(containerName, resourceName);
        Assert.Equal("original", second!.Metadata["key"]);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowOperationCanceledAndStoreNothing_WhenTokenIsAlreadyCancelled()
    {
        // Arrange
        var containerName = "containerName";
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Task act() => resourceRepository.AddResourceAsync(containerName, Version(containerName, "resourceName", DateTime.UtcNow), new MemoryStream([1, 2, 3]), cancellationToken: cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Empty(resourceDbContext.Resources);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowOperationCanceledAndStoreNothing_WhenTokenIsCancelledMidStream()
    {
        // Arrange
        var containerName = "containerName";
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        using var cts = new CancellationTokenSource();
        var content = new CancelAfterReadsStream(new byte[200_000], cts, readsBeforeCancel: 1);

        // Act
        Task act() => resourceRepository.AddResourceAsync(containerName, Version(containerName, "resourceName", DateTime.UtcNow), content, cancellationToken: cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Empty(resourceDbContext.Resources);
        Assert.Empty(resourceDbContext.ResourceChunks);
    }

    [Fact]
    public async Task GetAllResourceInfoAsync_ShouldThrowOperationCanceled_WhenTokenIsAlreadyCancelled()
    {
        // Arrange & Act
        (_, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resourceRepository.GetAllResourceInfoAsync("containerName", cts.Token));
    }

    [Fact]
    public async Task GetResourceStreamAsync_ShouldThrowOperationCanceled_WhenTokenIsAlreadyCancelled()
    {
        // Arrange & Act
        var containerName = "containerName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        await resourceRepository.AddResourceAsync(containerName, Version(containerName, "resourceName", DateTime.UtcNow), new MemoryStream([1, 2, 3]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resourceRepository.GetResourceStreamAsync(containerName, "resourceName", null, cts.Token));
    }

    [Fact]
    public async Task DeleteResourceContainerAsync_ShouldThrowOperationCanceledAndDeleteNothing_WhenTokenIsAlreadyCancelled()
    {
        // Arrange
        var containerName = "containerName";
        (_, var dbContextFactory) = NewDatabase();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Task act() => resourceRepository.DeleteResourceContainerAsync(containerName, cts.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.NotNull(await resourceRepository.GetResourceContainerInfoAsync(containerName));
    }

    [Fact]
    public async Task GetResourceVersionsAsync_ShouldPutTheLastStoredVersionFirst_WhenItsClockIsBehind()
    {
        // Arrange: the second writer's clock is an hour behind the first's.
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = "c" });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "ahead", LastModified = DateTime.UtcNow.AddHours(1) }, new MemoryStream());
        await resourceRepository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "behind", LastModified = DateTime.UtcNow }, new MemoryStream());

        // Act
        List<ResourceInfo> versions = await resourceRepository.GetResourceVersionsAsync("c", "n");

        // Assert
        Assert.Equal(["behind", "ahead"], versions.Select(v => v.VersionId));
        Assert.True(versions[0].IsLatest);
    }

    [Fact]
    public async Task DeleteResourceAsync_ShouldPromoteTheLastStoredRemainingVersion_WhenAnEarlierOneHasALaterClock()
    {
        // Arrange
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = "c" });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "first", LastModified = DateTime.UtcNow.AddHours(1) }, new MemoryStream());
        await resourceRepository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "second", LastModified = DateTime.UtcNow }, new MemoryStream());
        await resourceRepository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "third", LastModified = DateTime.UtcNow }, new MemoryStream());

        // Act
        await resourceRepository.DeleteResourceAsync("c", "n");

        // Assert
        Assert.Equal("second", (await resourceRepository.GetResourceInfoAsync("c", "n"))!.VersionId);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldThrowConflictAndStoreNothing_WhenCreateOnlyAndTheNameExists()
    {
        // Arrange
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = "c" });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        await resourceRepository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "first" }, new MemoryStream());

        // Act
        Task act() => resourceRepository.AddResourceAsync("c", new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "second" }, new MemoryStream(), createOnly: true);

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal("first", Assert.Single(await resourceRepository.GetResourceVersionsAsync("c", "n")).VersionId);
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStoreNothingMore_WhenTheSameVersionIsWrittenAgain()
    {
        // Arrange: a retried write whose first commit succeeded, including a create-only one that must not conflict
        // with itself.
        (ResourceDbContext resourceDbContext, var dbContextFactory) = NewDatabase();
        await resourceDbContext.ResourceContainers.AddAsync(new ResourceContainerModel { Name = "c" });
        await resourceDbContext.SaveChangesAsync();
        var resourceRepository = new ResourceSqlRepository(dbContextFactory);
        var version = new ResourceInfo { ContainerName = "c", Name = "n", VersionId = "v1" };
        await resourceRepository.AddResourceAsync("c", version, new MemoryStream(), createOnly: true);

        // Act
        await resourceRepository.AddResourceAsync("c", version, new MemoryStream(), createOnly: true);

        // Assert
        ResourceInfo stored = Assert.Single(await resourceRepository.GetResourceVersionsAsync("c", "n"));
        Assert.True(stored.IsLatest);
    }
}
