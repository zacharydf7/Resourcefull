using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [Fact]
    public async Task UploadAsync_ShouldThrowConflict_WhenTheNameExistsAndNoModeIsGiven()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        Task act() => container.UploadAsync("doc", BinaryData.FromString("v2"));

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Equal("v1", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [Fact]
    public async Task UploadAsync_ShouldReplaceTheOnlyVersion_WhenReplacingWithoutVersioning()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        ResourceInfo replacement = await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert
        ResourceInfo only = Assert.Single(await container.GetResourceVersionsAsync("doc"));
        Assert.Equal(replacement.VersionId, only.VersionId);
        Assert.Equal("v2", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowConflict_WhenTheNameExistsWithVersioningAndNoModeIsGiven()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));

        // Act
        Task act() => container.UploadAsync("doc", BinaryData.FromString("v2"));

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(act);
        Assert.Single(await container.GetResourceVersionsAsync("doc"));
    }

    [Fact]
    public async Task UploadAsync_ShouldKeepEachContainersOwnNumberOfVersions_WhenItsPolicySetsMaxVersions()
    {
        // Arrange: the store keeps three versions of each name; one container keeps one, and another two.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(3).UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string one = NewContainerName(), two = NewContainerName(), storeWide = NewContainerName();
        TestPolicyProvider.Policies[one] = new ResourceContainerPolicy { MaxVersions = 1 };
        TestPolicyProvider.Policies[two] = new ResourceContainerPolicy { MaxVersions = 2 };

        // Act
        var kept = new List<int>();
        foreach (string name in new[] { one, two, storeWide })
        {
            IResourceContainerClient container = await service.CreateContainerAsync(name);
            for (int i = 0; i < 4; i++)
                await container.UploadAsync("doc", BinaryData.FromString($"v{i}"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
            kept.Add((await container.GetResourceVersionsAsync("doc")).Count);
        }

        // Assert
        Assert.Equal([1, 2, 3], kept);
    }

    [Fact]
    public async Task UploadAsync_ShouldKeepOneLatestAndTrimToMaxVersions_WhenVersioningIsEnabled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(maxVersions: 2));
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        ResourceInfo third = await container.UploadAsync("doc", BinaryData.FromString("v3"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");
        Assert.Equal([third.VersionId, second.VersionId], versions.Select(v => v.VersionId));
        Assert.Single(versions, version => version.IsLatest);
        Assert.Equal("v3", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [Fact]
    public async Task GetResourceVersionsAsync_ShouldListTheCurrentVersionFirstThenNewestToOldest_WhenVersioningIsEnabled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        ResourceInfo third = await container.UploadAsync("doc", BinaryData.FromString("v3"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");

        // Assert
        Assert.Equal([third.VersionId, second.VersionId, first.VersionId], versions.Select(v => v.VersionId));
        Assert.Equal([true, false, false], versions.Select(v => v.IsLatest));
    }

    [Fact]
    public async Task DownloadContentAsync_ShouldReturnTheRequestedVersion_WhenGivenAnEarlierVersionId()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"));
        await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        ResourceContent? content = await container.DownloadContentAsync("doc", first.VersionId);

        // Assert
        Assert.Equal("v1", content!.Content.ToString());
        Assert.Equal(first.VersionId, content.ResourceInfo.VersionId);
        Assert.False(content.ResourceInfo.IsLatest);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldMakeThePreviousVersionCurrent_WhenTheCurrentVersionIsDeleted()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        ResourceInfo third = await container.UploadAsync("doc", BinaryData.FromString("v3"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("doc", third.VersionId);

        // Assert
        Assert.True(deleted);
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");
        Assert.Equal(2, versions.Count);
        Assert.Equal(second.VersionId, versions[0].VersionId);
        Assert.Equal([true, false], versions.Select(v => v.IsLatest));
        Assert.Equal("v2", (await container.DownloadContentAsync("doc"))!.Content.ToString());
        Assert.False(await container.DeleteResourceIfExistsAsync("doc", third.VersionId));
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldKeepTheCurrentVersion_WhenGivenAnOlderVersionId()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"));
        ResourceInfo second = await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("doc", first.VersionId);

        // Assert
        Assert.True(deleted);
        ResourceInfo current = Assert.Single(await container.GetResourceVersionsAsync("doc"));
        Assert.Equal(second.VersionId, current.VersionId);
        Assert.True(current.IsLatest);
        Assert.Equal("v2", (await container.DownloadContentAsync("doc"))!.Content.ToString());
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldDeleteEveryVersion_WhenNoVersionIsGiven()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("doc", BinaryData.FromString("v1"));
        await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("doc");

        // Assert
        Assert.True(deleted);
        Assert.Empty(await container.GetResourceVersionsAsync("doc"));
        Assert.Empty(await container.ListResourcesAsync().ToListAsync());
        Assert.False(await container.DeleteResourceIfExistsAsync("doc"));
    }

    [Fact]
    public async Task RestoreVersionAsync_ShouldStoreTheEarlierContentAndMetadataAsANewCurrentVersion_WhenGivenAnEarlierVersionId()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("doc", BinaryData.FromString("v1"), new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("text/plain", null),
            Metadata = new Dictionary<string, string> { ["draft"] = "1" }
        });
        await container.UploadAsync("doc", BinaryData.FromString("v2"), new ResourceUploadOptions
        { Mode = ResourceWriteMode.Replace,
            Headers = new ResourceHeaders("text/markdown", null),
            Metadata = new Dictionary<string, string> { ["draft"] = "2", ["reviewed"] = "yes" }
        });

        // Act
        ResourceInfo? restored = await container.RestoreVersionAsync("doc", first.VersionId);

        // Assert
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("doc");
        Assert.Equal(3, versions.Count);
        Assert.Equal(restored!.VersionId, versions[0].VersionId);
        Assert.NotEqual(first.VersionId, restored.VersionId);
        Assert.Equal(first.ContentHash, restored.ContentHash);
        ResourceContent current = (await container.DownloadContentAsync("doc"))!;
        Assert.Equal("v1", current.Content.ToString());
        Assert.Equal("text/plain", current.ResourceInfo.ContentType);
        Assert.Equal(new Dictionary<string, string> { ["draft"] = "1" }, current.ResourceInfo.Metadata);
    }

    [Fact]
    public async Task Versions_ShouldBeOrderedAndPromotedByWhenTheyWereStored_WhenTheWritersClocksDisagree()
    {
        // Arrange: "first" is stored by a server whose clock is an hour ahead of the ones storing the later two.
        var clock = new ManualTimeProvider();
        using ServiceProvider provider = BuildServiceProvider(o =>
        {
            o.UseVersioning();
            o.Services.AddSingleton<TimeProvider>(clock);
        });
        IResourceContainerClient container = await NewContainerAsync(provider);
        DateTimeOffset now = clock.Now;
        clock.Now = now.AddHours(1);
        ResourceInfo first = await container.UploadAsync("n", BinaryData.FromString("1"));
        clock.Now = now;
        ResourceInfo second = await container.UploadAsync("n", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        ResourceInfo third = await container.UploadAsync("n", BinaryData.FromString("3"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Act
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("n");
        ResourceInfo listed = Assert.Single(await container.ListResourcesAsync().ToListAsync());
        ResourceInfo paged = Assert.Single((await container.GetResourcePageAsync()).Resources);
        string content = (await container.DownloadContentAsync("n"))!.Content.ToString();
        await container.DeleteResourceIfExistsAsync("n", third.VersionId);
        ResourceInfo? promoted = await container.GetResourceInfoAsync("n");
        ResourceInfo listedAfter = Assert.Single(await container.ListResourcesAsync().ToListAsync());

        // Assert: the order they were stored in, not their LastModified, in every read.
        Assert.Equal([third.VersionId, second.VersionId, first.VersionId], versions.Select(v => v.VersionId));
        Assert.Equal((third.VersionId, third.VersionId, "3"), (listed.VersionId, paged.VersionId, content));
        Assert.Equal(second.VersionId, promoted!.VersionId);
        Assert.True(promoted.IsLatest);
        Assert.Equal(second.VersionId, listedAfter.VersionId);
        Assert.Equal("2", (await container.DownloadContentAsync("n"))!.Content.ToString());
    }
}
