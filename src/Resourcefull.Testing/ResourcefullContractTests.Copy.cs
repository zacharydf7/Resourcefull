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
            Headers = new ResourceHeaders("application/pdf", null),
            Metadata = new Dictionary<string, string> { ["k"] = "v" },
            ExpiresAt = SupportsExpiry ? DateTimeOffset.UtcNow.AddDays(1) : null
        });
        await source.UploadAsync("a.pdf", BinaryData.FromString("second"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await source.UploadAsync("b/c.txt", BinaryData.FromString("c"));
        string copyName = NewContainerName();

        // Act
        ContainerCopyResult copied = await service.CopyContainerToAsync(source.Name, service, copyName);

        // Assert
        Assert.Equal((2L, 3L, 12L), (copied.ResourceCount, copied.VersionCount, copied.ContentSize.Bytes));
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
        await source.UploadAsync("a.pdf", BinaryData.FromString("a"), new ResourceUploadOptions { Headers = new ResourceHeaders("application/pdf", null) });
        await source.UploadAsync("b.txt", BinaryData.FromString("b"), new ResourceUploadOptions { Headers = new ResourceHeaders("text/plain", null) });
        using ServiceProvider strict = BuildServiceProvider(o =>
            o.UseContainerPolicies(new ContainerPolicy { AllowedContentTypes = ["application/pdf"] }));
        string copyName = NewContainerName();

        // Act & Assert
        await Assert.ThrowsAsync<ResourcePolicyViolationException>(() =>
            ServiceOf(provider).CopyContainerToAsync(source.Name, ServiceOf(strict), copyName));
        Assert.False(await ServiceOf(strict).ContainerExistsAsync(copyName));
    }
}
