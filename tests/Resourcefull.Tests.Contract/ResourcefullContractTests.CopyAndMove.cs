using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task CopyResourceAsync_ShouldStoreANewVersionWithTheSameContentAndItsOrigin_WhenCopiedToAnotherContainer()
    {
        // Arrange: content over several chunks, in providers that store it in pieces.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient source = await NewContainerAsync(provider);
        IResourceContainerClient destination = await NewContainerAsync(provider);
        byte[] payload = RandomNumberGenerator.GetBytes(150_000);
        ResourceInfo original = await source.UploadAsync("report.pdf", new MemoryStream(payload), new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("application/pdf", null),
            Metadata = new Dictionary<string, string> { ["author"] = "ada" }
        });

        // Act
        ResourceInfo? copy = await source.CopyResourceAsync("report.pdf", destination.ResourceContainerInfo.Name, "archive/report.pdf");
        ResourceContent? content = await destination.DownloadContentAsync("archive/report.pdf");

        // Assert
        Assert.NotNull(copy);
        Assert.NotEqual(original.VersionId, copy.VersionId);
        Assert.Equal(new ResourceOrigin(source.ResourceContainerInfo.Name, "report.pdf", original.VersionId!), copy.CopiedFrom);
        Assert.Equal((original.ContentHash, original.ContentLength, "application/pdf"), (copy.ContentHash, copy.ContentLength, copy.ContentType));
        Assert.Equal(payload, content!.Content.ToArray());
        Assert.Equal("ada", content.ResourceInfo.Metadata["author"]);
        Assert.Equal(copy.CopiedFrom, content.ResourceInfo.CopiedFrom);
        Assert.Null((await source.GetResourceInfoAsync("report.pdf"))!.CopiedFrom);
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldCopyTheGivenVersion_WhenGivenAVersionId()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("notes.txt", BinaryData.FromString("first"));
        await container.UploadAsync("notes.txt", BinaryData.FromString("second"));

        // Act
        ResourceInfo? copy = await container.CopyResourceAsync("notes.txt", container.ResourceContainerInfo.Name, "copy.txt",
            new ResourceCopyOptions { VersionId = first.VersionId });

        // Assert
        Assert.Equal("first", (await container.DownloadContentAsync("copy.txt"))!.Content.ToString());
        Assert.Equal(first.VersionId, copy!.CopiedFrom!.VersionId);
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldRefuseAnExistingNameUnlessOverwriteIsSet_WhenVersioningIsOff()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("source"));
        await container.UploadAsync("b", BinaryData.FromString("existing"));
        string name = container.ResourceContainerInfo.Name;

        // Act
        Task refused() => container.CopyResourceAsync("a", name, "b");
        await Assert.ThrowsAsync<ResourceConflictException>(refused);
        string before = (await container.DownloadContentAsync("b"))!.Content.ToString();
        await container.CopyResourceAsync("a", name, "b", new ResourceCopyOptions { Overwrite = true });

        // Assert
        Assert.Equal("existing", before);
        Assert.Equal("source", (await container.DownloadContentAsync("b"))!.Content.ToString());
        Assert.Single(await container.GetResourceVersionsAsync("b"));
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldCopyOnlyIfTheDestinationStillMatches_WhenGivenDestinationConditions()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("source"));
        ResourceInfo current = await container.UploadAsync("b", BinaryData.FromString("existing"));
        string name = container.ResourceContainerInfo.Name;

        // Act
        Task stale() => container.CopyResourceAsync("a", name, "b",
            new ResourceCopyOptions { DestinationConditions = new ResourceConditions { IfMatchVersionId = "not-current" } });
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(stale);
        await container.CopyResourceAsync("a", name, "b",
            new ResourceCopyOptions { DestinationConditions = new ResourceConditions { IfMatchVersionId = current.VersionId } });

        // Assert
        Assert.Equal("source", (await container.DownloadContentAsync("b"))!.Content.ToString());
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldReturnNullOrThrowNotFound_WhenTheSourceOrDestinationContainerIsMissing()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("x"));

        // Act
        ResourceInfo? missing = await container.CopyResourceAsync("missing", container.ResourceContainerInfo.Name, "b");
        Task noContainer() => container.CopyResourceAsync("a", NewContainerName(), "b");

        // Assert
        Assert.Null(missing);
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(noContainer);
        Assert.False(await container.ExistsAsync("b"));
    }

    [SkippableFact]
    public async Task RestoreVersionAsync_ShouldRecordTheVersionItRestored_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("notes.txt", BinaryData.FromString("first"));
        await container.UploadAsync("notes.txt", BinaryData.FromString("second"));

        // Act
        ResourceInfo? restored = await container.RestoreVersionAsync("notes.txt", first.VersionId!);

        // Assert
        var origin = new ResourceOrigin(container.ResourceContainerInfo.Name, "notes.txt", first.VersionId!);
        Assert.Equal(origin, restored!.CopiedFrom);
        Assert.Equal(origin, (await container.GetResourceInfoAsync("notes.txt"))!.CopiedFrom);
    }

    [SkippableFact]
    public async Task MoveResourceAsync_ShouldMoveEveryVersionKeepingTheirIdsAndOrigins_WhenMovedToAnotherContainer()
    {
        // Arrange: two versions, the older one itself a copy.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient source = await NewContainerAsync(provider);
        IResourceContainerClient destination = await NewContainerAsync(provider);
        await source.UploadAsync("original", BinaryData.FromString("v1"));
        ResourceInfo first = (await source.CopyResourceAsync("original", source.ResourceContainerInfo.Name, "doc"))!;
        ResourceInfo second = await source.UploadAsync("doc", BinaryData.FromString("v2"));

        // Act
        ResourceInfo? moved = await source.MoveResourceAsync("doc", destination.ResourceContainerInfo.Name, "moved/doc");

        // Assert
        Assert.Equal(second.VersionId, moved!.VersionId);
        Assert.Equal(destination.ResourceContainerInfo.Name, moved.ContainerName);
        Assert.False(await source.ExistsAsync("doc"));
        IReadOnlyList<ResourceInfo> versions = await destination.GetResourceVersionsAsync("moved/doc");
        Assert.Equal([second.VersionId, first.VersionId], versions.Select(v => v.VersionId));
        Assert.Equal(first.CopiedFrom, versions[1].CopiedFrom);
        Assert.Equal(first.LastModified, versions[1].LastModified);
        Assert.Equal("v1", (await destination.DownloadContentAsync("moved/doc", first.VersionId))!.Content.ToString());
    }

    [SkippableFact]
    public async Task MoveResourceAsync_ShouldRenameTheResource_WhenMovedWithinItsContainer()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo stored = await container.UploadAsync("draft.txt", BinaryData.FromString("text"));

        // Act
        ResourceInfo? renamed = await container.MoveResourceAsync("draft.txt", container.ResourceContainerInfo.Name, "final.txt");

        // Assert
        Assert.Equal(("final.txt", stored.VersionId), (renamed!.Name, renamed.VersionId));
        Assert.False(await container.ExistsAsync("draft.txt"));
        Assert.Equal("text", (await container.DownloadContentAsync("final.txt"))!.Content.ToString());
    }

    [SkippableFact]
    public async Task MoveResourceAsync_ShouldChangeNothing_WhenTheDestinationExistsOrItsContainerIsMissingOrConditionsFail()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string name = container.ResourceContainerInfo.Name;
        await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.UploadAsync("b", BinaryData.FromString("b"));

        // Act
        Task existing() => container.MoveResourceAsync("a", name, "b");
        Task noContainer() => container.MoveResourceAsync("a", NewContainerName(), "a");
        Task stale() => container.MoveResourceAsync("a", name, "c",
            new ResourceMoveOptions { SourceConditions = new ResourceConditions { IfMatchVersionId = "not-current" } });
        Task itself() => container.MoveResourceAsync("a", name, "a");

        // Assert
        await Assert.ThrowsAsync<ResourceConflictException>(existing);
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(noContainer);
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(stale);
        await Assert.ThrowsAsync<ArgumentException>(itself);
        Assert.Equal(["a", "b"], (await container.GetResourcesAsync()).Select(r => r.Name).Order());
        Assert.Null(await container.MoveResourceAsync("missing", name, "c"));
    }

    [SkippableTheory]
    [InlineData("type")]
    [InlineData("size")]
    [InlineData("count")]
    [InlineData("readOnly")]
    public async Task MoveResourceAsync_ShouldBeRefusedForAnyVersion_WhenTheDestinationsPolicyForbidsIt(string rule)
    {
        // Arrange: the current version passes every rule; the older one is a different type and larger.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning().UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string from = NewContainerName(), to = NewContainerName();
        IResourceContainerClient source = await service.CreateResourceContainerAsync(from);
        IResourceContainerClient destination = await service.CreateResourceContainerAsync(to);
        await source.UploadAsync("doc", new MemoryStream(new byte[100]), Typed("text/html"));
        await source.UploadAsync("doc", new MemoryStream(new byte[10]), Typed("application/pdf"));
        await destination.UploadAsync("other", new MemoryStream(new byte[1]), Typed("application/pdf"));
        TestPolicyProvider.Policies[to] = rule switch
        {
            "type" => new ResourceContainerPolicy { AllowedContentTypes = ["application/pdf"] },
            "size" => new ResourceContainerPolicy { MaxResourceSize = 50 },
            "count" => new ResourceContainerPolicy { MaxResourceCount = 1 },
            _ => new ResourceContainerPolicy { ReadOnly = true }
        };

        // Act
        Exception? refused = await Record.ExceptionAsync(() => source.MoveResourceAsync("doc", to, "doc"));

        // Assert
        Assert.Equal(rule switch
        {
            "type" => ResourcePolicyRule.AllowedContentTypes,
            "size" => ResourcePolicyRule.MaxResourceSize,
            "count" => ResourcePolicyRule.MaxResourceCount,
            _ => ResourcePolicyRule.ReadOnly
        }, Assert.IsType<ResourcePolicyViolationException>(refused).Rule);
        Assert.Equal(2, (await source.GetResourceVersionsAsync("doc")).Count);
        Assert.False(await destination.ExistsAsync("doc"));
    }

    [SkippableFact]
    public async Task CopyResourceAsync_ShouldRefuseANewNameButNotANewVersion_WhenTheDestinationHoldsMaxResourceCount()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning()
            .UseContainerPolicies(new ResourceContainerPolicy { MaxResourceCount = 2 }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        string name = container.ResourceContainerInfo.Name;
        await container.UploadAsync("a", BinaryData.FromString("a"));
        await container.UploadAsync("b", BinaryData.FromString("b"));

        // Act
        Exception? newName = await Record.ExceptionAsync(() => container.CopyResourceAsync("a", name, "c"));
        ResourceInfo? newVersion = await container.CopyResourceAsync("a", name, "b");

        // Assert
        Assert.Equal(ResourcePolicyRule.MaxResourceCount, Assert.IsType<ResourcePolicyViolationException>(newName).Rule);
        Assert.NotNull(newVersion);
    }

    [SkippableFact]
    public async Task CopyAndMove_ShouldBeRefused_WhenAContainersPolicyForbidsThem()
    {
        // Arrange: a destination that takes only images and is nearly full, and a read-only source.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(cacheFor: TimeSpan.Zero)
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string from = NewContainerName(), to = NewContainerName();
        IResourceContainerClient source = await service.CreateResourceContainerAsync(from);
        await service.CreateResourceContainerAsync(to);
        await source.UploadAsync("notes.txt", BinaryData.FromString("text"), Typed("text/plain"));
        await source.UploadAsync("photo.png", new MemoryStream(new byte[100]), Typed("image/png"));
        TestPolicyProvider.Policies[to] = new ResourceContainerPolicy { AllowedContentTypes = ["image/*"], MaxContainerSize = 50 };

        // Act
        Exception? wrongType = await Record.ExceptionAsync(() => source.CopyResourceAsync("notes.txt", to, "notes.txt"));
        Exception? tooLarge = await Record.ExceptionAsync(() => source.MoveResourceAsync("photo.png", to, "photo.png"));
        TestPolicyProvider.Policies[from] = new ResourceContainerPolicy { ReadOnly = true };
        Exception? readOnly = await Record.ExceptionAsync(() => source.MoveResourceAsync("notes.txt", from, "renamed.txt"));

        // Assert
        Assert.Equal(ResourcePolicyRule.AllowedContentTypes, Assert.IsType<ResourcePolicyViolationException>(wrongType).Rule);
        Assert.Equal(ResourcePolicyRule.MaxContainerSize, Assert.IsType<ResourcePolicyViolationException>(tooLarge).Rule);
        Assert.Equal(ResourcePolicyRule.ReadOnly, Assert.IsType<ResourcePolicyViolationException>(readOnly).Rule);
        Assert.Equal(["notes.txt", "photo.png"], (await source.GetResourcesAsync()).Select(r => r.Name).Order());
    }
}
