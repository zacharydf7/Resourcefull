using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Testing;

public abstract partial class ResourcefullContractTests
{
    // More than the pipe to the inspector holds, and more than one batch for providers that store content in batches.
    private const int InspectedSize = 3 * 1024 * 1024;

    /// <summary>An upload inspector whose answers each test sets for its own containers; others are accepted.</summary>
    private sealed class TestUploadInspector : IResourceUploadInspector
    {
        public static readonly ConcurrentDictionary<string,
            Func<ResourceUploadInspection, CancellationToken, Task<ResourceInspectionResult>>> Answers = new();

        public async ValueTask<ResourceInspectionResult> InspectAsync(ResourceUploadInspection upload,
            CancellationToken cancellationToken) =>
            Answers.TryGetValue(upload.ContainerName, out var answer)
                ? await answer(upload, cancellationToken)
                : ResourceInspectionResult.Accepted;
    }

    private ServiceProvider BuildInspectedServiceProvider() =>
        BuildServiceProvider(o => o.UseVersioning().UseUploadInspector<TestUploadInspector>());

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreWhatTheInspectorRead_WhenItAcceptsTheContent()
    {
        // Arrange
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceUploadInspection? seen = null;
        byte[]? read = null;
        TestUploadInspector.Answers[container.Name] = async (upload, cancellationToken) =>
        {
            seen = upload;
            read = await ReadAllAsync(upload.Content, cancellationToken);
            return ResourceInspectionResult.Accepted;
        };
        byte[] payload = RandomNumberGenerator.GetBytes(InspectedSize);
        var options = new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("application/pdf", payload.Length),
            Metadata = new Dictionary<string, string> { ["author"] = "ada" }
        };

        // Act
        ResourceInfo stored = await container.UploadAsync("report.pdf", new MemoryStream(payload), options);

        // Assert
        Assert.Equal(payload, read);
        Assert.Equal(payload, (await container.DownloadContentAsync("report.pdf"))!.Content.ToArray());
        Assert.Equal((container.Name, "report.pdf", stored.VersionId, "application/pdf", (long?)payload.Length, "ada"),
            (seen!.ContainerName, seen.ResourceName, seen.VersionId, seen.ContentType, seen.ContentLength, seen.Metadata["author"]));
    }

    [Fact]
    public async Task UploadAsync_ShouldNotShowTheVersion_WhenTheInspectorHasReadEverythingButNotYetAnswered()
    {
        // Arrange: a declared length, so a provider that reads only that much never waits for the answer.
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ResourceInspectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestUploadInspector.Answers[container.Name] = async (upload, cancellationToken) =>
        {
            await ReadAllAsync(upload.Content, cancellationToken);
            read.SetResult();
            return await answer.Task;
        };
        byte[] payload = RandomNumberGenerator.GetBytes(InspectedSize);
        Task<ResourceInfo> upload = container.UploadAsync("report.pdf", new MemoryStream(payload),
            new ResourceUploadOptions { Headers = new ResourceHeaders("application/pdf", payload.Length) });
        // Bounded, and a failed upload throws its own exception rather than leaving this to wait.
        await await Task.WhenAny(read.Task, upload).WaitAsync(TimeSpan.FromSeconds(30));

        // Act
        ResourceInfo? whileDeciding = await container.GetResourceInfoAsync("report.pdf");
        answer.SetResult(ResourceInspectionResult.Accepted);
        await upload;

        // Assert
        Assert.Null(whileDeciding);
        Assert.True(await container.ExistsAsync("report.pdf"));
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreNothingAndKeepTheCurrentVersion_WhenTheInspectorRefusesTheContent()
    {
        // Arrange: the inspector reads the whole upload before it refuses, so the provider has read it all too.
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("report.pdf", BinaryData.FromString("kept"));
        TestUploadInspector.Answers[container.Name] = async (upload, cancellationToken) =>
        {
            await ReadAllAsync(upload.Content, cancellationToken);
            return ResourceInspectionResult.Refused("It looks like malware.");
        };

        // Act
        Task act() => container.UploadAsync("report.pdf", new MemoryStream(RandomNumberGenerator.GetBytes(InspectedSize)), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert
        var refused = await Assert.ThrowsAsync<ResourceContentRefusedException>(act);
        Assert.Equal("It looks like malware.", refused.Reason);
        Assert.EndsWith("It looks like malware.", refused.Message);
        Assert.Equal((container.Name, "report.pdf"), (refused.ContainerName, refused.ResourceName));
        Assert.Equal("kept", (await container.DownloadContentAsync("report.pdf"))!.Content.ToString());
        Assert.Single(await container.GetResourceVersionsAsync("report.pdf"));
        Assert.Equal(1, (await container.GetUsageAsync()).VersionCount);
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreNothing_WhenTheInspectorRefusesAfterReadingOnlyTheStart()
    {
        // Arrange
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        TestUploadInspector.Answers[container.Name] = async (upload, cancellationToken) =>
        {
            await upload.Content.ReadExactlyAsync(new byte[4], cancellationToken);
            return ResourceInspectionResult.Refused("It isn't a PDF.");
        };
        var content = new MemoryStream(RandomNumberGenerator.GetBytes(InspectedSize));

        // Act
        Task act() => container.UploadAsync("report.pdf", content);

        // Assert: the rest wasn't read.
        Assert.Equal("It isn't a PDF.", (await Assert.ThrowsAsync<ResourceContentRefusedException>(act)).Reason);
        Assert.False(await container.ExistsAsync("report.pdf"));
        Assert.True(content.Position < InspectedSize);
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreAllOfIt_WhenTheInspectorAcceptsAfterReadingOnlyTheStart()
    {
        // Arrange
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        TestUploadInspector.Answers[container.Name] = async (upload, cancellationToken) =>
        {
            await upload.Content.ReadExactlyAsync(new byte[4], cancellationToken);
            return ResourceInspectionResult.Accepted;
        };
        byte[] payload = RandomNumberGenerator.GetBytes(InspectedSize);

        // Act
        await container.UploadAsync("report.pdf", new MemoryStream(payload));

        // Assert
        Assert.Equal(payload, (await container.DownloadContentAsync("report.pdf"))!.Content.ToArray());
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreNothingAndPassOnTheException_WhenTheInspectorThrows()
    {
        // Arrange
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        TestUploadInspector.Answers[container.Name] = (_, _) =>
            throw new InvalidOperationException("The scanner is unavailable.");

        // Act
        Task act() => container.UploadAsync("report.pdf", BinaryData.FromString("x"));

        // Assert
        Assert.Equal("The scanner is unavailable.", (await Assert.ThrowsAsync<InvalidOperationException>(act)).Message);
        Assert.False(await container.ExistsAsync("report.pdf"));
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldLeaveNoContainer_WhenTheInspectorRefusesAVersion()
    {
        // Arrange: an export of a container whose uploads were accepted, imported into one whose aren't.
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        IResourceContainerClient source = await NewContainerAsync(provider);
        await source.UploadAsync("report.pdf", BinaryData.FromString("x"));
        var export = new MemoryStream();
        await service.ExportContainerAsync(source.Name, export);
        export.Position = 0;
        string restoredName = NewContainerName();
        TestUploadInspector.Answers[restoredName] = (_, _) => Task.FromResult(ResourceInspectionResult.Refused("No."));

        // Act
        Task act() => service.ImportContainerAsync(restoredName, export);

        // Assert
        await Assert.ThrowsAsync<ResourceContentRefusedException>(act);
        Assert.False(await service.ContainerExistsAsync(restoredName));
    }

    [Fact]
    public async Task RestoreCopyAndMove_ShouldNotBeInspected_WhenTheContentIsAlreadyStored()
    {
        // Arrange: versions stored before the inspector starts refusing everything.
        using ServiceProvider provider = BuildInspectedServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.pdf", BinaryData.FromString("first"));
        await container.UploadAsync("report.pdf", BinaryData.FromString("second"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        int inspected = 0;
        TestUploadInspector.Answers[container.Name] = (_, _) =>
        {
            Interlocked.Increment(ref inspected);
            return Task.FromResult(ResourceInspectionResult.Refused("No."));
        };

        // Act
        await container.RestoreVersionAsync("report.pdf", first.VersionId);
        await container.CopyResourceAsync("report.pdf", container.Name, "copy.pdf");
        await container.MoveResourceAsync("copy.pdf", container.Name, "moved.pdf");

        // Assert
        Assert.Equal(0, inspected);
        Assert.Equal("first", (await container.DownloadContentAsync("moved.pdf"))!.Content.ToString());
    }
}
