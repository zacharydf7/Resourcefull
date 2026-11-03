using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Resourcefull.InMemory;
using Resourcefull.Providers;
using Resourcefull.Tests.Contract;

namespace Resourcefull.Tests.Unit;

public class UploadInspectionTests
{
    // More than the pipe to the inspector holds.
    private static readonly byte[] Payload = new byte[3 * 1024 * 1024];

    /// <summary>In-memory services whose uploads <paramref name="answer"/> inspects.</summary>
    internal static ServiceProvider InspectedServices(
        Func<ResourceUploadInspection, CancellationToken, Task<ResourceInspectionResult>> answer) =>
        new ServiceCollection().AddResourcefull(o => DelegatingUploadInspector.Register(o.UseInMemory(), answer))
            .BuildServiceProvider();

    [Fact]
    public async Task UploadAsync_ShouldStopTheInspectorAndWaitForIt_WhenTheUploadFailsBeforeItAnswers()
    {
        // Arrange: an inspector that waits until it's stopped, and content that fails partway.
        bool stopped = false;
        using ServiceProvider provider = InspectedServices(async (upload, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return ResourceInspectionResult.Accepted;
            }
            finally
            {
                stopped = cancellationToken.IsCancellationRequested;
            }
        });
        IResourceContainerClient container =
            await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");

        // Act
        Task act() => container.UploadAsync("r", new FailingStream(Payload, failAt: 100_000));

        // Assert
        await Assert.ThrowsAsync<IOException>(act);
        Assert.True(stopped);
        Assert.False(await container.ExistsAsync("r"));
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreTheContent_WhenTheInspectorReadsItSynchronously()
    {
        // Arrange
        using ServiceProvider provider = InspectedServices((upload, _) =>
        {
            upload.Content.CopyTo(Stream.Null);
            return Task.FromResult(ResourceInspectionResult.Accepted);
        });
        IResourceContainerClient container =
            await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");

        // Act
        ResourceInfo stored = await container.UploadAsync("r", new MemoryStream(Payload)).WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(Payload.Length, stored.ContentLength);
    }

    [Fact]
    public async Task UploadAsync_ShouldDeleteTheVersion_WhenAProviderStoresItWithoutReadingToTheEndAndTheInspectorRefusesIt()
    {
        // Arrange: a provider that reads only the declared length, so it never makes the read that waits for the answer.
        var store = new ResourceInMemoryRepository();
        await store.AddContainerAsync("c", new Dictionary<string, string>());
        var repository = new Mock<ResourceRepository>();
        repository.Setup(r => r.AddResourceAsync( It.IsAny<NewResourceVersion>(), It.IsAny<Stream>(), It.IsAny<ResourceWriteCondition?>(), It.IsAny<CancellationToken>()))
            .Returns(async (NewResourceVersion version, Stream content, ResourceWriteCondition? condition,
                CancellationToken cancellationToken) =>
            {
                byte[] declared = new byte[version.DeclaredLength!.Value];
                await content.ReadExactlyAsync(declared, cancellationToken);
                return await store.AddResourceAsync(version, new MemoryStream(declared), condition, cancellationToken);
            });
        repository.Setup(r => r.DeleteResourceAsync(new ResourceLocation("c", "r"), It.IsAny<string?>(), It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()))
            .Returns((ResourceLocation resource, string? versionId, ResourceConditions? conditions, CancellationToken cancellationToken) =>
                store.DeleteResourceAsync(resource, versionId, conditions, cancellationToken));
        var inspector = new DelegatingUploadInspector(async (upload, cancellationToken) =>
        {
            await upload.Content.CopyToAsync(Stream.Null, cancellationToken);
            return ResourceInspectionResult.Refused("It looks like malware.");
        });
        IResourceContainerClient container = new ResourceServiceClient(repository.Object,
            Options.Create(new ResourcefullOptions()), uploadInspector: inspector).GetContainerClient("c");

        // Act
        Task act() => container.UploadAsync("r", new MemoryStream(Payload),
            new ResourceUploadOptions { Headers = new ResourceHeaders(null, Payload.Length) });

        // Assert
        await Assert.ThrowsAsync<ResourceContentRefusedException>(act);
        Assert.Null(await store.GetResourceInfoAsync(new ResourceLocation("c", "r")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Refused_ShouldThrow_WhenGivenNoReason(string reason)
    {
        // Act
        Action act = () => ResourceInspectionResult.Refused(reason);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    /// <summary>Content that throws once more than <paramref name="failAt"/> bytes have been read.</summary>
    private sealed class FailingStream(byte[] content, int failAt) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > failAt) throw new IOException("The connection was reset.");
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
