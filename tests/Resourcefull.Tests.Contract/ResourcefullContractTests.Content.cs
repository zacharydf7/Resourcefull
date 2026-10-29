using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task UploadAsync_ShouldRoundTripContentAndMetadata_WhenDownloaded()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        // Large enough to span several chunks in providers that store content in pieces.
        byte[] payload = RandomNumberGenerator.GetBytes(150_000);
        var options = new ResourceUploadOptions
        {
            Headers = new ResourceHeaders("application/pdf", null),
            Metadata = new Dictionary<string, string> { ["author"] = "ada" }
        };

        // Act
        ResourceInfo uploaded = await container.UploadAsync("report.pdf", new MemoryStream(payload), options);
        ResourceContent? content = await container.DownloadContentAsync("report.pdf");

        // Assert
        Assert.Equal(payload, content!.Content.ToArray());
        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(payload)), uploaded.ContentHash);
        Assert.Equal(uploaded.VersionId, content.ResourceInfo.VersionId);
        Assert.Equal("application/pdf", content.ResourceInfo.ContentType);
        Assert.Equal(payload.Length, content.ResourceInfo.ContentLength);
        Assert.Equal("ada", content.ResourceInfo.Metadata["author"]);
        Assert.True(content.ResourceInfo.IsLatest);
        Assert.Equal(uploaded.ContentHash, content.ResourceInfo.ContentHash);
        ResourceInfo listed = Assert.Single(await container.GetResourcesAsync());
        Assert.Equal("report.pdf", listed.Name);
        Assert.Equal(uploaded.ContentHash, listed.ContentHash);
        Assert.Equal(uploaded.ContentHash, Assert.Single(await container.GetResourceVersionsAsync("report.pdf")).ContentHash);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldReturnAndStoreTheMeasuredLength_WhenTheDeclaredLengthIsRightOrAbsent()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        byte[] payload = new byte[70_000];
        Random.Shared.NextBytes(payload);

        // Act
        ResourceInfo undeclared = await container.UploadAsync("undeclared.bin", new MemoryStream(payload));
        ResourceInfo declared = await container.UploadAsync("declared.bin", new MemoryStream(payload),
            new ResourceUploadOptions { Headers = new ResourceHeaders("application/octet-stream", payload.Length) });
        Exception? wrong = await Record.ExceptionAsync(() => container.UploadAsync("wrong.bin", new MemoryStream(payload),
            new ResourceUploadOptions { Headers = new ResourceHeaders("application/octet-stream", payload.Length + 1) }));

        // Assert
        Assert.Equal(payload.Length, undeclared.ContentLength);
        Assert.Equal(payload.Length, (await container.GetResourceInfoAsync("undeclared.bin"))!.ContentLength);
        Assert.Equal(undeclared.ContentHash, declared.ContentHash);
        Assert.IsType<ResourceContentLengthMismatchException>(wrong);
        Assert.False(await container.ExistsAsync("wrong.bin"));
    }

    [SkippableFact]
    public async Task DownloadStreamingAsync_ShouldReturnAReadOnlyForwardOnlyStream_WhenTheResourceExists()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("data.bin", new MemoryStream(new byte[100]));

        // Act
        ResourceDownloadResult? result = await container.DownloadStreamingAsync("data.bin");
        await using Stream stream = result!.Stream!;
        int read = await stream.ReadAsync(new byte[10]);

        // Assert
        Assert.True(stream.CanRead);
        Assert.False(stream.CanWrite);
        Assert.False(stream.CanSeek);
        Assert.Equal(100, stream.Length);
        Assert.Equal(read, stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Position = 0);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldStampTheTimeTheVersionBecameCurrent_WhenTheClockMovesOnWhileTheContentIsRead()
    {
        // Arrange: the clock moves on as the content is read, as it would during a large or slow upload.
        var clock = new ManualTimeProvider();
        using ServiceProvider provider = BuildServiceProvider(o => o.Services.AddSingleton<TimeProvider>(clock));
        IResourceContainerClient container = await NewContainerAsync(provider);
        DateTimeOffset readFinished = clock.Now.AddMinutes(5);
        var content = new AdvanceClockOnReadStream(new byte[100], clock, readFinished);

        // Act
        ResourceInfo uploaded = await container.UploadAsync("doc", content);

        // Assert
        Assert.Equal(readFinished, uploaded.LastModified);
        Assert.Equal(readFinished, (await container.GetResourceInfoAsync("doc"))!.LastModified);
    }

    [SkippableFact]
    public async Task DownloadContentAsync_ShouldReturnNull_WhenTheResourceDoesNotExist()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        ResourceContent? content = await container.DownloadContentAsync("missing.txt");

        // Assert
        Assert.Null(content);
        Assert.False(await container.ExistsAsync("missing.txt"));
    }

    /// <summary>Sets <paramref name="clock"/> to <paramref name="readTime"/> when it is first read.</summary>
    private sealed class AdvanceClockOnReadStream(byte[] content, ManualTimeProvider clock, DateTimeOffset readTime) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            clock.Now = readTime;
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
