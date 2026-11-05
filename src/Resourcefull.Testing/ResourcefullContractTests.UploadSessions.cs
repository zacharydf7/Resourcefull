using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    [Fact]
    public async Task CompleteUploadAsync_ShouldStoreThePartsAsOneVersion_WhenTheContentWasAppendedInParts()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: parts of different sizes, the middle one larger than a provider's batch.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        byte[][] parts = [RandomNumberGenerator.GetBytes(100), RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 7), RandomNumberGenerator.GetBytes(1)];
        byte[] whole = [.. parts.SelectMany(p => p)];
        ResourceUpload upload = await container.StartUploadAsync("video.mp4", new ResourceUploadSessionOptions
        {
            Headers = new ResourceHeaders("video/mp4", whole.Length),
            Metadata = new Dictionary<string, string> { ["source"] = "camera" }
        });

        // Act
        foreach (byte[] part in parts)
            upload = (await container.AppendUploadAsync("video.mp4", upload.UploadId, upload.Offset, new MemoryStream(part)))!;
        ResourceInfo? stored = await container.CompleteUploadAsync("video.mp4", upload.UploadId);

        // Assert
        Assert.Equal(whole.Length, upload.Offset);
        Assert.Equal(upload.UploadId, stored!.VersionId);
        Assert.Equal((whole.LongLength, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(whole))), (stored.ContentLength!.Value, stored.ContentHash));
        Assert.Equal("video/mp4", stored.ContentType);
        Assert.Equal("camera", stored.Metadata["source"]);
        Assert.Equal(whole, (await container.DownloadContentAsync("video.mp4"))!.Content.ToArray());
        Assert.Null(await container.GetUploadAsync("video.mp4", upload.UploadId));
    }

    [Fact]
    public async Task UploadSession_ShouldShowNothingOfTheVersion_WhenItIsNotCompleted()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload upload = await container.StartUploadAsync("a");
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream([1, 2, 3]));

        // Act
        ResourceInfo? info = await container.GetResourceInfoAsync("a");
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("a");
        List<ResourceInfo> listed = await container.ListResourcesAsync().ToListAsync();
        ResourceContainerUsage usage = await container.GetUsageAsync();
        IReadOnlyList<ResourceInfo> largest = await container.GetTopVersionsAsync(ResourceVersionOrder.LargestFirst);
        ResourceUpload? session = await container.GetUploadAsync("a", upload.UploadId);

        // Assert
        Assert.Null(info);
        Assert.Empty(versions);
        Assert.Empty(listed);
        Assert.Equal((0L, 0L), (usage.VersionCount, usage.StoredSize.Bytes));
        Assert.Empty(largest);
        Assert.Equal(3, session!.Offset);
    }

    [Fact]
    public async Task AppendUploadAsync_ShouldRefuseAndStoreNothing_WhenTheOffsetIsNotWhereTheContentEnds()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: an append whose answer was lost is sent again from the same offset.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload upload = await container.StartUploadAsync("a");
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream([1, 2, 3]));

        // Act
        var again = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() =>
            container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream([1, 2, 3])));
        var ahead = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() =>
            container.AppendUploadAsync("a", upload.UploadId, 5, new MemoryStream([4])));

        // Assert
        Assert.Equal((3L, 3L), (again.CurrentOffset, ahead.CurrentOffset));
        Assert.Equal(3, (await container.GetUploadAsync("a", upload.UploadId))!.Offset);
        await container.CompleteUploadAsync("a", upload.UploadId);
        Assert.Equal([1, 2, 3], (await container.DownloadContentAsync("a"))!.Content.ToArray());
    }

    [Fact]
    public async Task CompleteUploadAsync_ShouldStoreAnEmptyVersion_WhenNothingWasAppended()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload upload = await container.StartUploadAsync("empty.txt");

        // Act
        ResourceInfo? stored = await container.CompleteUploadAsync("empty.txt", upload.UploadId);

        // Assert
        Assert.Equal(0, stored!.ContentLength);
        Assert.Empty((await container.DownloadContentAsync("empty.txt"))!.Content.ToArray());
    }

    [Fact]
    public async Task CompleteUploadAsync_ShouldRefuseAndKeepTheSession_WhenTheWriteModeOrConditionsAreNotMet()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: the name has a current version, so a create-only completion is refused.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo existing = await container.UploadAsync("a", BinaryData.FromString("old"));
        ResourceUpload upload = await container.StartUploadAsync("a", new ResourceUploadSessionOptions());
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream("new"u8.ToArray()));

        // Act
        await Assert.ThrowsAsync<ResourceConflictException>(() => container.CompleteUploadAsync("a", upload.UploadId));
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(() => container.CompleteUploadAsync("a", upload.UploadId,
            ResourceWriteMode.Replace, new ResourceConditions { IfMatchVersionId = "not-the-current-version" }));
        ResourceInfo? replaced = await container.CompleteUploadAsync("a", upload.UploadId, ResourceWriteMode.Replace,
            new ResourceConditions { IfMatchVersionId = existing.VersionId });

        // Assert
        Assert.Equal(upload.UploadId, replaced!.VersionId);
        Assert.Equal("new", (await container.DownloadContentAsync("a"))!.Content.ToString());
        Assert.Equal(2, (await container.GetResourceVersionsAsync("a")).Count);
    }

    [Fact]
    public async Task CancelUploadAsync_ShouldDeleteTheSession_WhenCalled()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload upload = await container.StartUploadAsync("a");
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream([1]));

        // Act
        bool canceled = await container.CancelUploadAsync("a", upload.UploadId);

        // Assert: every later call finds no session.
        Assert.True(canceled);
        Assert.Null(await container.GetUploadAsync("a", upload.UploadId));
        Assert.Null(await container.AppendUploadAsync("a", upload.UploadId, 1, new MemoryStream([2])));
        Assert.Null(await container.CompleteUploadAsync("a", upload.UploadId));
        Assert.False(await container.CancelUploadAsync("a", upload.UploadId));
        Assert.False(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task CompleteUploadAsync_ShouldDeleteTheSessionAndStoreNothing_WhenTheInspectorRefusesTheContent()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: the inspector reads the content the session stored, as it does an upload's.
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        byte[] content = RandomNumberGenerator.GetBytes(InspectedSize);
        byte[]? inspected = null;
        TestUploadInspector.Answers[container.Name] = async (upload, cancellationToken) =>
        {
            inspected = await ReadAllAsync(upload.Content, cancellationToken);
            return ResourceInspectionResult.Refused("Not allowed.");
        };
        ResourceUpload upload = await container.StartUploadAsync("a");
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream(content));

        // Act
        await Assert.ThrowsAsync<ResourceContentRefusedException>(() => container.CompleteUploadAsync("a", upload.UploadId));

        // Assert
        Assert.Equal(content, inspected);
        Assert.False(await container.ExistsAsync("a"));
        Assert.Null(await container.GetUploadAsync("a", upload.UploadId));
    }

    [Fact]
    public async Task CompleteUploadAsync_ShouldRefuseAndKeepTheSession_WhenADifferentLengthWasAppendedThanDeclared()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: a session that said it would hold 5 bytes, with 3 appended.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload upload = await container.StartUploadAsync("a", new ResourceUploadSessionOptions
        {
            Headers = new ResourceHeaders(null, 5)
        });
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream([1, 2, 3]));

        // Act
        await Assert.ThrowsAsync<ResourceContentLengthMismatchException>(() => container.CompleteUploadAsync("a", upload.UploadId));
        ResourceUpload? kept = await container.GetUploadAsync("a", upload.UploadId);
        await container.AppendUploadAsync("a", upload.UploadId, 3, new MemoryStream([4, 5]));
        ResourceInfo? stored = await container.CompleteUploadAsync("a", upload.UploadId);

        // Assert
        Assert.Equal((3L, (long?)5), (kept!.Offset, kept.DeclaredLength));
        Assert.Equal(5, stored!.ContentLength);
    }

    [Fact]
    public async Task UploadSession_ShouldApplyTheContainersPolicy_WhenStartedAppendedToAndCompleted()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: a version may hold 10 bytes, the container one resource, of PDFs only.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(new ResourceContainerPolicy
        {
            MaxResourceSize = 10, MaxResourceCount = 1, AllowedContentTypes = ["application/pdf"]
        }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        var pdf = new ResourceUploadSessionOptions { Headers = new ResourceHeaders("application/pdf", null) };

        // Act & Assert: the start refuses a type or an expected length the policy refuses.
        await Assert.ThrowsAsync<ResourcePolicyViolationException>(() => container.StartUploadAsync("a",
            new ResourceUploadSessionOptions { Headers = new ResourceHeaders("text/html", null) }));
        await Assert.ThrowsAsync<ResourcePolicyViolationException>(() => container.StartUploadAsync("a",
            new ResourceUploadSessionOptions { Headers = new ResourceHeaders("application/pdf", 11) }));

        // An append stops at the size limit, counting what's already stored.
        ResourceUpload upload = await container.StartUploadAsync("a", pdf);
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream(new byte[6]));
        var tooLarge = await Assert.ThrowsAsync<ResourcePolicyViolationException>(() =>
            container.AppendUploadAsync("a", upload.UploadId, 6, new MemoryStream(new byte[6])));
        Assert.Equal(ResourcePolicyRule.MaxResourceSize, tooLarge.Rule);
        Assert.Equal(6, (await container.GetUploadAsync("a", upload.UploadId))!.Offset);

        // Completing a new name in a full container is refused, and the session stays.
        await container.UploadAsync("other.pdf", BinaryData.FromString("x"),
            new ResourceUploadOptions { Headers = new ResourceHeaders("application/pdf", null) });
        var full = await Assert.ThrowsAsync<ResourcePolicyViolationException>(() => container.CompleteUploadAsync("a", upload.UploadId));
        Assert.Equal(ResourcePolicyRule.MaxResourceCount, full.Rule);
        Assert.NotNull(await container.GetUploadAsync("a", upload.UploadId));
    }

    [Fact]
    public async Task AppendUploadAsync_ShouldStopAtTheContainersSize_WhenOnlyThatIsLimited()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: the container holds 10 bytes in all, and no limit is set for one resource.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(new ResourceContainerPolicy
        {
            MaxContainerSize = 10
        }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload upload = await container.StartUploadAsync("a");
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream(new byte[6]));

        // Act
        var exception = await Assert.ThrowsAsync<ResourcePolicyViolationException>(() =>
            container.AppendUploadAsync("a", upload.UploadId, 6, new MemoryStream(new byte[6])));

        // Assert
        Assert.Equal(ResourcePolicyRule.MaxContainerSize, exception.Rule);
        Assert.Equal(6, (await container.GetUploadAsync("a", upload.UploadId))!.Offset);
    }

    [Fact]
    public async Task CompleteStagedVersionAsync_ShouldThrowAndLeaveTheVersionStaged_WhenGivenALengthOtherThanWhatsStored()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: the clients measured 3 bytes, and an append stored 2 more before the completion.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        var location = new ResourceAddress(container.Name, "a");
        ResourceUpload upload = await container.StartUploadAsync("a");
        await container.AppendUploadAsync("a", upload.UploadId, 0, new MemoryStream([1, 2, 3, 4, 5]));

        // Act
        var exception = await Assert.ThrowsAsync<ResourceUploadOffsetException>(() =>
            repository.CompleteStagedVersionAsync(location, upload.UploadId, 3, "sha256:measured"));

        // Assert
        Assert.Equal(5, exception.CurrentOffset);
        Assert.Equal(5, (await repository.GetStagedVersionAsync(location, upload.UploadId))!.StoredLength);
        Assert.False(await container.ExistsAsync("a"));
    }

    [Fact]
    public async Task DeleteExpiredAsync_ShouldSweepASession_WhenNothingWasAppendedForADay()
    {
        Requires(SupportsUploadSessions, "upload sessions");

        // Arrange: two sessions, one appended to again after 20 hours.
        var clock = new ManualTimeProvider { Now = DateTimeOffset.UtcNow };
        using ServiceProvider provider = BuildServiceProvider(o => o.Services.AddSingleton<TimeProvider>(clock));
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUpload idle = await container.StartUploadAsync("idle");
        ResourceUpload active = await container.StartUploadAsync("active");
        clock.Now = clock.Now.AddHours(20);
        await container.AppendUploadAsync("active", active.UploadId, 0, new MemoryStream([1]));
        clock.Now = clock.Now.AddHours(5);

        // Act
        await ServiceOf(provider).DeleteExpiredAsync();

        // Assert
        Assert.Null(await container.GetUploadAsync("idle", idle.UploadId));
        Assert.Equal(1, (await container.GetUploadAsync("active", active.UploadId))!.Offset);
    }
}
