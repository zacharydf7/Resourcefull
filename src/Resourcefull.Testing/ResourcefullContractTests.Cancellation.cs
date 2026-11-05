using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull.Testing;

/// <summary>
/// What a provider must do with a canceled token, and with the dictionaries it's given and returns, checked on the
/// provider itself: the clients copy some of these dictionaries and check some tokens first, which would hide a
/// provider that doesn't.
/// </summary>
public abstract partial class ResourcefullContractTests
{
    [Theory]
    [InlineData("upload")]
    [InlineData("delete")]
    [InlineData("create container")]
    [InlineData("delete container")]
    [InlineData("list")]
    [InlineData("read")]
    public async Task Repository_ShouldThrowOperationCanceledAndChangeNothing_WhenTheTokenIsAlreadyCanceled(string operation)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string containerName = container.Name;
        await container.UploadAsync("kept", BinaryData.FromString("x"));
        string newContainer = NewContainerName();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        Task act() => operation switch
        {
            "upload" => repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation(containerName, "new"), "v1"),
                new MemoryStream([1, 2, 3]), null, canceled.Token),
            "delete" => repository.DeleteResourceAsync(new ResourceLocation(containerName, "kept"), null, null, canceled.Token),
            "create container" => repository.AddContainerAsync(newContainer, new Dictionary<string, string>(), canceled.Token),
            "delete container" => repository.DeleteContainerAsync(containerName, canceled.Token),
            "list" => repository.GetAllResourceInfoAsync(containerName, canceled.Token),
            "read" => repository.GetResourceStreamAsync(new ResourceLocation(containerName, "kept"), null, canceled.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        // Assert: a container delete may stop between whole versions, so only the container itself must remain.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Null(await repository.GetResourceInfoAsync(new ResourceLocation(containerName, "new")));
        if (operation != "delete container") Assert.NotNull(await repository.GetResourceInfoAsync(new ResourceLocation(containerName, "kept")));
        Assert.NotNull(await repository.GetContainerInfoAsync(containerName));
        Assert.Null(await repository.GetContainerInfoAsync(newContainer));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStopReadingThrowOperationCanceledAndStoreNothing_WhenCanceledWhileTheContentIsRead()
    {
        // Arrange: content whose first read cancels the upload's token.
        using ServiceProvider provider = BuildServiceProvider();
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string containerName = container.Name;
        using var cancellation = new CancellationTokenSource();
        var content = new InterruptedStream(new byte[1_000_000], () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        // Act
        Task act() => repository.AddResourceAsync(new NewResourceVersion(new ResourceLocation(containerName, "n"), "v1"),
            content, null, cancellation.Token);

        // Assert: it stops reading too, rather than reading a large upload to its end before giving up.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.True(content.Position < content.Length);
        Assert.Null(await repository.GetResourceInfoAsync(new ResourceLocation(containerName, "n")));
    }

    [Fact]
    public async Task Repository_ShouldKeepItsOwnCopyOfContainerMetadata_WhenTheCallersDictionaryChanges()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        string containerName = NewContainerName();
        var given = new Dictionary<string, string> { ["owner"] = "billing" };
        await repository.AddContainerAsync(containerName, given);

        // Act
        given["owner"] = "changed";

        // Assert
        Assert.Equal("billing", (await repository.GetContainerInfoAsync(containerName))!.Metadata["owner"]);
    }

    [Fact]
    public async Task Repository_ShouldKeepItsOwnCopyOfResourceMetadata_WhenTheCallersDictionaryChanges()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        ResourceRepository repository = provider.GetRequiredService<ResourceRepository>();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string containerName = container.Name;
        var given = new Dictionary<string, string> { ["author"] = "finance" };
        var location = new ResourceLocation(containerName, "n");
        await repository.AddResourceAsync(new NewResourceVersion(location, "v1") { Metadata = given }, new MemoryStream([1]));

        // Act
        given["author"] = "changed";

        // Assert
        Assert.Equal("finance", (await repository.GetResourceInfoAsync(location))!.Metadata["author"]);
    }
}
