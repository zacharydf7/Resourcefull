using Microsoft.Extensions.DependencyInjection;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit;

public class ClientInterfaceTests
{
    [Fact]
    public async Task ClientMembers_ShouldTakeTheirConsistentNames_WhenCalledWithNamedArguments()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddKeyedResourcefull(storeName: "files", configure: options => options.UseInMemory());
        using ServiceProvider provider = services.BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredKeyedService<IResourceServiceClient>("files");
        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string> { ["team"] = "billing" };

        // Act
        IResourceContainerClient first = await service.CreateContainerAsync(containerName: "first", metadata: metadata);
        IResourceContainerClient second = await service.CreateContainerIfNotExistsAsync(containerName: "second", metadata: metadata);
        await first.UploadAsync("a", BinaryData.FromString("a"), new ResourceUploadOptions { ExpiresAt = DateTimeOffset.UnixEpoch });
        await first.SetResourceMetadataAsync("a", null, metadata);
        await first.CopyResourceAsync("a", destinationContainerName: second.Name, destinationName: "b");
        await first.MoveResourceAsync("a", destinationContainerName: second.Name, destinationName: "c");
        IReadOnlyList<ResourceInfo> expired = await service.GetExpiredVersionsAsync(count: 10);

        // Assert
        Assert.Equal(metadata, (await first.GetInfoAsync())!.Metadata);
        Assert.Equal(metadata, (await second.GetInfoAsync())!.Metadata);
        Assert.Equal(metadata, (await second.GetResourceInfoAsync("b"))!.Metadata);
        Assert.Equal("c", Assert.Single(expired).Name);
    }

    [Fact]
    public void ClientInterfaces_ShouldHaveDefaultsOnlyOnMembersBuiltOnOthers_WhenCalled()
    {
        // Act
        string[] withDefaults = [.. new[] { typeof(IResourceContainerClient), typeof(IResourceServiceClient) }
            .SelectMany(type => type.GetMethods())
            .Where(method => !method.IsAbstract)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)];

        // Assert: each of these is built on the abstract members, so an implementation that leaves one out still behaves
        // correctly. A member added after 4.0 joins this list, with a default that either works or throws
        // NotSupportedException.
        Assert.Equal([nameof(IResourceServiceClient.ListContainersAsync), nameof(IResourceContainerClient.ListDeletedAsync),
            nameof(IResourceContainerClient.ListResourcesAsync), nameof(IResourceContainerClient.RenameResourceAsync)], withDefaults);
    }
}
