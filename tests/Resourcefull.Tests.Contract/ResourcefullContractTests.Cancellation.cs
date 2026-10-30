using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

/// <summary>
/// What a provider must do with a canceled token, and with the dictionaries it's given and returns, checked on the
/// provider itself: the clients copy some of these dictionaries and check some tokens first, which would hide a
/// provider that doesn't.
/// </summary>
public abstract partial class ResourcefullContractTests
{
    [SkippableTheory]
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
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string containerName = container.ResourceContainerInfo.Name;
        await container.UploadAsync("kept", BinaryData.FromString("x"));
        string newContainer = NewContainerName();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        Task act() => operation switch
        {
            "upload" => repository.AddResourceAsync(containerName, new ResourceInfo { ContainerName = containerName, Name = "new", VersionId = "v1" },
                new MemoryStream([1, 2, 3]), false, canceled.Token),
            "delete" => repository.DeleteResourceAsync(containerName, "kept", null, canceled.Token),
            "create container" => repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = newContainer }, canceled.Token),
            "delete container" => repository.DeleteResourceContainerAsync(containerName, canceled.Token),
            "list" => repository.GetAllResourceInfoAsync(containerName, canceled.Token),
            "read" => repository.GetResourceStreamAsync(containerName, "kept", null, canceled.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        // Assert: a container delete may stop between whole versions, so only the container itself must remain.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.Null(await repository.GetResourceInfoAsync(containerName, "new"));
        if (operation != "delete container") Assert.NotNull(await repository.GetResourceInfoAsync(containerName, "kept"));
        Assert.NotNull(await repository.GetResourceContainerInfoAsync(containerName));
        Assert.Null(await repository.GetResourceContainerInfoAsync(newContainer));
    }

    [SkippableFact]
    public async Task AddResourceAsync_ShouldStopReadingThrowOperationCanceledAndStoreNothing_WhenCanceledWhileTheContentIsRead()
    {
        // Arrange: content whose first read cancels the upload's token.
        using ServiceProvider provider = BuildServiceProvider();
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string containerName = container.ResourceContainerInfo.Name;
        using var cancellation = new CancellationTokenSource();
        var content = new InterruptedStream(new byte[1_000_000], () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        // Act
        Task act() => repository.AddResourceAsync(containerName, new ResourceInfo { ContainerName = containerName, Name = "n", VersionId = "v1" },
            content, false, cancellation.Token);

        // Assert: it stops reading too, rather than reading a large upload to its end before giving up.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
        Assert.True(content.Position < content.Length);
        Assert.Null(await repository.GetResourceInfoAsync(containerName, "n"));
    }

    [SkippableFact]
    public async Task Repository_ShouldKeepItsOwnCopyOfContainerMetadata_WhenTheCallersDictionaryChanges()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        string containerName = NewContainerName();
        var given = new Dictionary<string, string> { ["owner"] = "billing" };
        await repository.AddResourceContainerAsync(new ResourceContainerInfo { Name = containerName, Metadata = given });

        // Act: change the dictionary given, and the ones a read and a listing return.
        given["owner"] = "changed";
        (await repository.GetResourceContainerInfoAsync(containerName))!.Metadata["owner"] = "changed";
        (await repository.GetAllResourceContainerInfoAsync()).Single(c => c.Name == containerName).Metadata["owner"] = "changed";

        // Assert
        Assert.Equal("billing", (await repository.GetResourceContainerInfoAsync(containerName))!.Metadata["owner"]);
    }

    [SkippableFact]
    public async Task Repository_ShouldKeepItsOwnCopyOfResourceMetadata_WhenTheCallersDictionaryChanges()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider();
        IResourceRepository repository = provider.GetRequiredService<IResourceRepository>();
        IResourceContainerClient container = await NewContainerAsync(provider);
        string containerName = container.ResourceContainerInfo.Name;
        var given = new Dictionary<string, string> { ["author"] = "finance" };
        await repository.AddResourceAsync(containerName, new ResourceInfo { ContainerName = containerName, Name = "n", VersionId = "v1", Metadata = given },
            new MemoryStream([1]));

        // Act: change both the dictionary given and the one returned.
        given["author"] = "changed";
        (await repository.GetResourceInfoAsync(containerName, "n"))!.Metadata["author"] = "changed";

        // Assert
        Assert.Equal("finance", (await repository.GetResourceInfoAsync(containerName, "n"))!.Metadata["author"]);
    }
}
