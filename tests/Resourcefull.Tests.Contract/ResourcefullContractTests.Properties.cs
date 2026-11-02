using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    [SkippableFact]
    public async Task SetResourceMetadataAsync_ShouldReplaceOnlyThatVersionsMetadata_WhenGivenAVersion()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.pdf", BinaryData.FromString("first"),
            new ResourceUploadOptions { Metadata = new Dictionary<string, string> { ["author"] = "ada" } });
        ResourceInfo second = await container.UploadAsync("report.pdf", BinaryData.FromString("second"),
            new ResourceUploadOptions { Metadata = new Dictionary<string, string> { ["author"] = "ada" } });
        var metadata = new Dictionary<string, string> { ["reviewed"] = "yes" };

        // Act: the dictionary passed in is changed afterward, which mustn't change what's stored.
        bool set = await container.SetResourceMetadataAsync("report.pdf", first.VersionId, metadata);
        metadata["reviewed"] = "no";

        // Assert: the version is changed in place, keeping everything but its metadata.
        Assert.True(set);
        ResourceInfo changed = (await container.GetResourceInfoAsync("report.pdf", first.VersionId))!;
        Assert.Equal(new Dictionary<string, string> { ["reviewed"] = "yes" }, changed.Metadata);
        Assert.Equal((first.ContentHash, first.LastModified), (changed.ContentHash, changed.LastModified));
        Assert.Equal("ada", (await container.GetResourceInfoAsync("report.pdf"))!.Metadata["author"]);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("report.pdf")).Count);
        Assert.Equal(second.VersionId, (await container.GetResourceInfoAsync("report.pdf"))!.VersionId);
    }

    [SkippableFact]
    public async Task SetResourceMetadataAsync_ShouldChangeTheCurrentVersion_WhenGivenNoVersion()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo first = await container.UploadAsync("report.pdf", BinaryData.FromString("first"));
        await container.UploadAsync("report.pdf", BinaryData.FromString("second"));

        // Act
        bool set = await container.SetResourceMetadataAsync("report.pdf", null, new Dictionary<string, string> { ["k"] = "v" });

        // Assert
        Assert.True(set);
        Assert.Equal("v", (await container.GetResourceInfoAsync("report.pdf"))!.Metadata["k"]);
        Assert.Empty((await container.GetResourceInfoAsync("report.pdf", first.VersionId))!.Metadata);
    }

    [SkippableFact]
    public async Task SetContentTypeAsync_ShouldChangeTheTypeInPlace_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        ResourceInfo stored = await container.UploadAsync("report.pdf", BinaryData.FromString("%PDF-"),
            new ResourceUploadOptions { Headers = new ResourceHeaders("application/octet-stream", null) });

        // Act
        bool set = await container.SetContentTypeAsync("report.pdf", stored.VersionId, "application/pdf");
        ResourceContent? changed = await container.DownloadContentAsync("report.pdf");
        bool cleared = await container.SetContentTypeAsync("report.pdf", null, null);

        // Assert
        Assert.True(set && cleared);
        Assert.Equal("application/pdf", changed!.ResourceInfo.ContentType);
        Assert.Equal((stored.VersionId, stored.ContentHash, stored.LastModified),
            (changed.ResourceInfo.VersionId, changed.ResourceInfo.ContentHash, changed.ResourceInfo.LastModified));
        Assert.Equal("%PDF-", changed.Content.ToString());
        Assert.Null((await container.GetResourceInfoAsync("report.pdf"))!.ContentType);
    }

    [SkippableFact]
    public async Task SetResourceMetadataAndContentType_ShouldReturnFalse_WhenTheVersionDoesNotExist()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("report.pdf", BinaryData.FromString("x"));
        var metadata = new Dictionary<string, string>();

        // Act
        bool[] found =
        [
            await container.SetResourceMetadataAsync("missing.pdf", null, metadata),
            await container.SetResourceMetadataAsync("report.pdf", "no-such-version", metadata),
            await container.SetContentTypeAsync("missing.pdf", null, "application/pdf"),
            await container.SetContentTypeAsync("report.pdf", "no-such-version", "application/pdf")
        ];

        // Assert
        Assert.All(found, Assert.False);
        Assert.False(await container.ExistsAsync("missing.pdf"));
    }

    [SkippableFact]
    public async Task SetContentTypeAsync_ShouldBeRefusedAndChangeNothing_WhenTheContainerDoesNotAllowTheType()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(
            new ResourceContainerPolicy { AllowedContentTypes = ["image/png"] }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("logo.png", BinaryData.FromString("x"), Typed("image/png"));

        // Act
        Task act() => container.SetContentTypeAsync("logo.png", null, "text/html");

        // Assert
        var refused = await Assert.ThrowsAsync<ResourcePolicyViolationException>(act);
        Assert.Equal(ResourcePolicyRule.AllowedContentTypes, refused.Rule);
        Assert.Equal("image/png", (await container.GetResourceInfoAsync("logo.png"))!.ContentType);
    }

    [SkippableFact]
    public async Task SetResourceContainerMetadataAsync_ShouldReplaceTheMetadata_WhenTheContainerExists()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        await service.CreateResourceContainerAsync(name, new Dictionary<string, string> { ["owner"] = "billing" });

        // Act
        bool set = await service.SetResourceContainerMetadataAsync(name, new Dictionary<string, string> { ["owner"] = "finance" });
        bool missing = await service.SetResourceContainerMetadataAsync(NewContainerName(), new Dictionary<string, string>());

        // Assert
        Assert.True(set);
        Assert.False(missing);
        Assert.Equal(new Dictionary<string, string> { ["owner"] = "finance" },
            (await service.GetResourceContainerInfoAsync(name))!.Metadata);
    }
}
