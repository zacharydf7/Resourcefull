namespace Resourcefull.Tests.Unit;

public class ClientInterfaceTests
{
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
