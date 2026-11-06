using Microsoft.Extensions.DependencyInjection;
using Resourcefull.FileSystem;
using Resourcefull.Providers;

namespace Resourcefull.Tests.FileSystem;

/// <summary>A provider that wraps a built-in one, as one that caches or encrypts would.</summary>
[Collection(FileSystemCollection.Name)]
public class WrappedProviderTests(FileSystemFixture fixture)
{
    [Fact]
    public async Task ListResourcesAsync_ShouldFindAVersionByMetadata_WhenTheProviderWrapsABuiltInOne()
    {
        // Arrange: the file system's provider, which indexes searchable keys, wrapped in one that passes calls on.
        IServiceCollection services = new ServiceCollection();
        services.AddResourcefull(options => options.UseFileSystem(fixture.NewRoot()).UseMetadataSearch("customer"));
        ServiceDescriptor registered = services.Single(d => d.ServiceType == typeof(ResourceRepository));
        services.Remove(registered);
        services.Add(new ServiceDescriptor(typeof(ResourceRepository),
            provider => new PassingRepository((ResourceRepository)registered.ImplementationFactory!(provider)), registered.Lifetime));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a", BinaryData.FromString("x"),
            new ResourceUploadOptions { Metadata = new Dictionary<string, string> { ["customer"] = "42" } });

        // Act
        List<ResourceInfo> found = await container.ListResourcesAsync(new ResourceListOptions
        {
            MetadataMatch = new ResourceMetadataMatch("customer", "42")
        }).ToListAsync();

        // Assert: the wrapped provider indexed the key, so its own search found the version.
        Assert.Equal("a", Assert.Single(found).Name);
    }

    /// <summary>Passes the abstract members and the search on, so the wrapped provider's index answers searches.</summary>
    private sealed class PassingRepository(ResourceRepository inner) : ResourceRepository
    {
        public override Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
            ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) =>
            inner.AddResourceAsync(version, content, condition, cancellationToken);

        public override Task<IReadOnlyList<ResourceInfo>> GetResourcePageAsync(string containerName, string? prefix,
            string? afterName, int pageSize, CancellationToken cancellationToken = default) =>
            inner.GetResourcePageAsync(containerName, prefix, afterName, pageSize, cancellationToken);

        public override Task<IReadOnlyList<ResourceInfo>> FindResourcePageAsync(string containerName, string? prefix,
            string? afterName, int pageSize, ResourceMetadataMatch match, CancellationToken cancellationToken = default) =>
            inner.FindResourcePageAsync(containerName, prefix, afterName, pageSize, match, cancellationToken);

        public override Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceAddress resource,
            CancellationToken cancellationToken = default) =>
            inner.GetResourceVersionsAsync(resource, cancellationToken);

        public override Task<ResourceInfo?> GetResourceInfoAsync(ResourceAddress resource, string? versionId = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResourceInfoAsync(resource, versionId, cancellationToken);

        public override Task<Stream?> GetResourceStreamAsync(ResourceAddress resource, string? versionId = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResourceStreamAsync(resource, versionId, cancellationToken);

        public override Task<bool> DeleteResourceAsync(ResourceAddress resource, string? versionId = null,
            ResourceConditions? conditions = null, CancellationToken cancellationToken = default) =>
            inner.DeleteResourceAsync(resource, versionId, conditions, cancellationToken);

        public override Task AddContainerAsync(string containerName, IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) =>
            inner.AddContainerAsync(containerName, metadata, cancellationToken);

        public override Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName, int pageSize,
            CancellationToken cancellationToken = default) =>
            inner.GetContainerPageAsync(prefix, afterName, pageSize, cancellationToken);

        public override Task<ContainerInfo?> GetContainerInfoAsync(string containerName,
            CancellationToken cancellationToken = default) =>
            inner.GetContainerInfoAsync(containerName, cancellationToken);

        public override Task<bool> DeleteContainerAsync(string containerName, CancellationToken cancellationToken = default) =>
            inner.DeleteContainerAsync(containerName, cancellationToken);
    }
}
