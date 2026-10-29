using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    private static ResourceUploadOptions IfMatch(string? versionId = null, string? contentHash = null) =>
        new() { Conditions = new ResourceConditions { IfMatchVersionId = versionId, IfMatchContentHash = contentHash } };

    [SkippableFact]
    public async Task UploadAsync_ShouldStoreANewCurrentVersion_WhenIfMatchVersionIdIsTheCurrentVersion()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        ResourceInfo second = await container.UploadAsync("report.txt", BinaryData.FromString("two"), IfMatch(first.VersionId));

        // Assert
        Assert.Equal(second.VersionId, (await container.GetResourceInfoAsync("report.txt"))!.VersionId);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("report.txt")).Count);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowPreconditionFailedAndStoreNothing_WhenIfMatchVersionIdIsNotTheCurrentVersion()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.txt", BinaryData.FromString("one"));
        ResourceInfo second = await container.UploadAsync("report.txt", BinaryData.FromString("two"));

        // Act
        Task act() => container.UploadAsync("report.txt", BinaryData.FromString("three"), IfMatch(first.VersionId));

        // Assert
        var exception = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Equal(second.VersionId, exception.CurrentVersionId);
        Assert.Equal(second.ContentHash, exception.CurrentContentHash);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("report.txt")).Count);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowPreconditionFailed_WhenAnotherUploadReplacesTheMatchedVersionWhileItsContentIsRead()
    {
        // Arrange: the competing upload lands after any check made before the content is read, so only the check the
        // write makes as the version would become current can refuse it.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo read = await container.UploadAsync("report.txt", BinaryData.FromString("one"));
        ResourceInfo? competitor = null;
        var content = new InterruptedStream("two"u8.ToArray(),
            async () => competitor = await container.UploadAsync("report.txt", BinaryData.FromString("other")));

        // Act
        Task act() => container.UploadAsync("report.txt", content, IfMatch(read.VersionId));

        // Assert
        var exception = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Equal(competitor!.VersionId, exception.CurrentVersionId);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("report.txt")).Count);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowContainerNotFound_WhenAConditionalUploadTargetsAMissingContainer()
    {
        // Arrange: a missing container is reported as such, not as unmet conditions.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = ServiceOf(provider).GetResourceContainerClient(NewContainerName());

        // Act
        Task act() => container.UploadAsync("report.txt", BinaryData.FromString("x"), IfMatch(Guid.NewGuid().ToString()));

        // Assert
        await Assert.ThrowsAsync<ResourceContainerNotFoundException>(act);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldThrowPreconditionFailedAndStoreNothing_WhenTheNameHasNoCurrentVersion()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Task act() => container.UploadAsync("report.txt", BinaryData.FromString("x"), IfMatch(Guid.NewGuid().ToString()));

        // Assert
        var exception = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Null(exception.CurrentVersionId);
        Assert.False(await container.ExistsAsync("report.txt"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldStoreOrRefuseByTheCurrentContentHash_WhenGivenIfMatchContentHash()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        ResourceInfo second = await container.UploadAsync("report.txt", BinaryData.FromString("two"), IfMatch(contentHash: first.ContentHash));
        Task stale() => container.UploadAsync("report.txt", BinaryData.FromString("three"), IfMatch(contentHash: first.ContentHash));

        // Assert
        var exception = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(stale);
        Assert.Equal(second.ContentHash, exception.CurrentContentHash);
        Assert.Equal(second.VersionId, (await container.GetResourceInfoAsync("report.txt"))!.VersionId);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldRequireBothConditions_WhenGivenAVersionIdAndAContentHash()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        Task act() => container.UploadAsync("report.txt", BinaryData.FromString("two"), IfMatch(first.VersionId, "sha256:00"));

        // Assert
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Single(await container.GetResourceVersionsAsync("report.txt"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldReplaceTheCurrentVersion_WhenItsConditionsHoldWithoutVersioningOrOverwrite()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.txt", BinaryData.FromString("one"));

        // Act
        await container.UploadAsync("report.txt", BinaryData.FromString("two"), IfMatch(first.VersionId));

        // Assert
        Assert.Equal("two", (await container.DownloadContentAsync("report.txt"))!.Content.ToString());
        Assert.Single(await container.GetResourceVersionsAsync("report.txt"));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldLetExactlyOneSucceed_WhenConcurrentUploadsMatchTheSameCurrentVersion()
    {
        // Arrange: every writer read the same version and tries to replace it.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning(maxVersions: Writers + 1));
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo read = await container.UploadAsync("report.txt", BinaryData.FromString("x"));

        // Act
        Exception?[] outcomes = await RaceAsync(provider, i =>
            container.UploadAsync("report.txt", BinaryData.FromString($"writer {i}"), IfMatch(read.VersionId)));

        // Assert
        Assert.Single(outcomes, o => o is null);
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ResourcePreconditionFailedException>(o));
        Assert.Equal(2, (await container.GetResourceVersionsAsync("report.txt")).Count);
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldDelete_WhenTheConditionsHold()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo current = await container.UploadAsync("report.txt", BinaryData.FromString("x"));

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("report.txt", null,
            new ResourceConditions { IfMatchVersionId = current.VersionId, IfMatchContentHash = current.ContentHash }, CancellationToken.None);

        // Assert
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("report.txt"));
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldLetExactlyOneSucceed_WhenConcurrentDeletesMatchTheSameCurrentVersion()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo read = await container.UploadAsync("report.txt", BinaryData.FromString("x"));

        // Act
        Exception?[] outcomes = await RaceAsync(provider, _ => container.DeleteResourceIfExistsAsync("report.txt", null,
            new ResourceConditions { IfMatchVersionId = read.VersionId }, CancellationToken.None));

        // Assert
        Assert.Single(outcomes, o => o is null);
        Assert.All(outcomes.Where(o => o is not null), o => Assert.IsType<ResourcePreconditionFailedException>(o));
        Assert.False(await container.ExistsAsync("report.txt"));
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldThrowPreconditionFailedAndDeleteNothing_WhenTheCurrentVersionDoesNotMatch()
    {
        // Arrange: the version to delete is an older one, but the conditions are on the current version.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.txt", BinaryData.FromString("one"));
        ResourceInfo second = await container.UploadAsync("report.txt", BinaryData.FromString("two"));

        // Act
        Task act() => container.DeleteResourceIfExistsAsync("report.txt", first.VersionId,
            new ResourceConditions { IfMatchVersionId = first.VersionId }, CancellationToken.None);

        // Assert
        var exception = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Equal(second.VersionId, exception.CurrentVersionId);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("report.txt")).Count);
    }

    [SkippableFact]
    public async Task DeleteResourceIfExistsAsync_ShouldThrowPreconditionFailed_WhenTheNameDoesNotExist()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Task act() => container.DeleteResourceIfExistsAsync("report.txt", null,
            new ResourceConditions { IfMatchVersionId = Guid.NewGuid().ToString() }, CancellationToken.None);

        // Assert
        var exception = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Null(exception.CurrentVersionId);
    }

    /// <summary>Content whose first read runs another operation, as if it happened while an upload read its content.</summary>
    private sealed class InterruptedStream(byte[] content, Func<Task> interruption) : MemoryStream(content)
    {
        private bool _interrupted;

        public override int Read(byte[] buffer, int offset, int count)
        {
            InterruptAsync().GetAwaiter().GetResult();
            return base.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await InterruptAsync();
            return await base.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await InterruptAsync();
            return await base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        private async Task InterruptAsync()
        {
            if (_interrupted) return;
            _interrupted = true;
            await interruption();
        }
    }
}
