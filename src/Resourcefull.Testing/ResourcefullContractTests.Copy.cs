using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    [Fact]
    public async Task CopyContainerToAsync_ShouldCopyEveryVersionAsItWas_WhenCopyingUnderAnotherName()
    {
        Requires(SupportsImport, "imports");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a.pdf", BinaryData.FromString("first"), new ResourceUploadOptions
        {
            Headers = new ResourceHeaders { ContentType = "application/pdf" },
            Metadata = new Dictionary<string, string> { ["k"] = "v" },
            ExpiresAt = SupportsExpiry ? DateTimeOffset.UtcNow.AddDays(1) : null
        });
        await source.UploadAsync("a.pdf", BinaryData.FromString("second"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await source.UploadAsync("b/c.txt", BinaryData.FromString("c"));
        string copyName = NewContainerName();

        // Act
        ContainerCopyResult copied = await service.CopyContainerToAsync(source.Name, service, new ContainerCopyOptions { DestinationName = copyName });

        // Assert
        Assert.Equal((ContainerCopyOutcome.Copied, 2L, 3L, 12L), (copied.Outcome, copied.ResourceCount, copied.VersionCount, copied.ContentSize.Bytes));
        Assert.Null(copied.Update);
        IResourceContainerClient copy = service.GetContainerClient(copyName);
        foreach (string name in new[] { "a.pdf", "b/c.txt" })
        {
            Assert.Equivalent((await source.GetResourceVersionsAsync(name)).Select(v => v with { ContainerName = "", Uri = null }),
                (await copy.GetResourceVersionsAsync(name)).Select(v => v with { ContainerName = "", Uri = null }), strict: true);
        }
        Assert.Equal("second", (await copy.DownloadContentAsync("a.pdf"))!.Content.ToString());
        await service.DeleteContainerIfExistsAsync(copyName);
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldDeleteWhatItCopied_WhenTheDestinationRefusesAVersion()
    {
        Requires(SupportsImport, "imports");

        // Arrange: a destination store whose policy refuses the second version's type.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a.pdf", BinaryData.FromString("a"), new ResourceUploadOptions { Headers =
            new ResourceHeaders { ContentType = "application/pdf" } });
        await source.UploadAsync("b.txt", BinaryData.FromString("b"), new ResourceUploadOptions { Headers =
            new ResourceHeaders { ContentType = "text/plain" } });
        using ServiceProvider strict = BuildServiceProvider(o =>
            o.UseContainerPolicies(new ContainerPolicy { AllowedContentTypes = ["application/pdf"] }));
        string copyName = NewContainerName();

        // Act & Assert
        await Assert.ThrowsAsync<ResourcePolicyViolationException>(() =>
            ServiceOf(provider).CopyContainerToAsync(source.Name, ServiceOf(strict), new ContainerCopyOptions { DestinationName = copyName }));
        Assert.False(await ServiceOf(strict).ContainerExistsAsync(copyName));
    }

    /// <summary>The names in a container, with their versions' IDs and contents' hashes, to compare two containers.</summary>
    private static async Task<List<(string Name, string VersionId, string? Hash)>> VersionsInAsync(IResourceContainerClient container)
    {
        var versions = new List<(string, string, string?)>();
        await foreach (ResourceInfo resource in container.ListResourcesAsync())
        {
            foreach (ResourceInfo version in await container.GetResourceVersionsAsync(resource.Name))
                versions.Add((version.Name, version.VersionId, version.ContentHash));
        }
        return versions;
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldRefuseAndChangeNothing_WhenTheDestinationHasTheContainerAndExistingIsFail()
    {
        Requires(SupportsImport, "imports");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("ours", BinaryData.FromString("ours"));
        IResourceContainerClient theirs = await NewContainerAsync(provider);
        await theirs.UploadAsync("theirs", BinaryData.FromString("theirs"));
        var options = new ContainerCopyOptions { DestinationName = theirs.Name };

        // Act
        ResourceConflictException refused = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            service.CopyContainerToAsync(source.Name, service, options));

        // Assert: it's the default, and the exception names the container that's in the way.
        Assert.Equal(ExistingContainers.Fail, options.Existing);
        Assert.Equal(theirs.Name, refused.ContainerName);
        Assert.Equal(["theirs"], (await theirs.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldFindAMatchThenRefuseADifference_WhenExistingIsSkipMatching()
    {
        Requires(SupportsImport, "imports");

        // Arrange: a copy of the container.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("a", BinaryData.FromString("a"));
        string copyName = NewContainerName();
        var options = new ContainerCopyOptions { DestinationName = copyName, Existing = ExistingContainers.SkipMatching };
        await service.CopyContainerToAsync(source.Name, service, new ContainerCopyOptions { DestinationName = copyName });

        // Act
        ContainerCopyResult matching = await service.CopyContainerToAsync(source.Name, service, options);
        await source.UploadAsync("b", BinaryData.FromString("b"));
        ResourceConflictException refused = await Assert.ThrowsAsync<ResourceConflictException>(() =>
            service.CopyContainerToAsync(source.Name, service, options));

        // Assert: nothing was copied either time, and the refusal names what differs.
        Assert.Equal((ContainerCopyOutcome.Matching, 0L, 0L, 0L), (matching.Outcome, matching.ResourceCount, matching.VersionCount, matching.ContentSize.Bytes));
        Assert.Null(matching.Update);
        Assert.Contains("b isn't in the destination", refused.Message);
        Assert.Equal(["a"], (await service.GetContainerClient(copyName).ListResourcesAsync().ToListAsync()).Select(r => r.Name));
        await service.DeleteContainerIfExistsAsync(copyName);
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldBringTheContainerUpToDateThenFindItMatches_WhenExistingIsUpdate()
    {
        Requires(SupportsImport, "imports");
        Requires(SupportsVersionChanges, "changing a version's content type");

        // Arrange: a copy, then in the source a new version, a new name, a changed version and a deleted name.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("grows", BinaryData.FromString("one"));
        await source.UploadAsync("changes", BinaryData.FromString("c"), new ResourceUploadOptions { Headers = new ResourceHeaders { ContentType = "text/plain" } });
        await source.UploadAsync("goes", BinaryData.FromString("g"));
        string copyName = NewContainerName();
        var options = new ContainerCopyOptions { DestinationName = copyName, Existing = ExistingContainers.Update };
        await service.CopyContainerToAsync(source.Name, service, new ContainerCopyOptions { DestinationName = copyName });
        await source.UploadAsync("grows", BinaryData.FromString("three"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await source.UploadAsync("new", BinaryData.FromString("new!"));
        await source.SetContentTypeAsync("changes", null, "text/html");
        await source.DeleteResourceIfExistsAsync("goes");

        // Act
        ContainerCopyResult updated = await service.CopyContainerToAsync(source.Name, service, options);
        ContainerCopyResult again = await service.CopyContainerToAsync(source.Name, service, options);

        // Assert
        Assert.Equal((source.Name, copyName), (updated.SourceContainerName, updated.DestinationContainerName));
        Assert.Equal((ContainerCopyOutcome.Updated, 2L, 2L, 9L), (updated.Outcome, updated.ResourceCount, updated.VersionCount, updated.ContentSize.Bytes));
        Assert.Equal(new ContainerUpdateResult { Name = copyName, Imported = 2, Changed = 1, Deleted = 1 }, updated.Update);
        IResourceContainerClient copy = service.GetContainerClient(copyName);
        Assert.Equal(await VersionsInAsync(source), await VersionsInAsync(copy));
        Assert.Equal("text/html", (await copy.GetResourceInfoAsync("changes"))!.ContentType);
        Assert.Equal(ContainerCopyOutcome.Matching, again.Outcome);
        Assert.Null(again.Update);
        await service.DeleteContainerIfExistsAsync(copyName);
    }
}
