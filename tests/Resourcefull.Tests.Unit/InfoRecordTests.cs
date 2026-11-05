namespace Resourcefull.Tests.Unit;

/// <summary><see cref="ResourceInfo"/> and <see cref="ContainerInfo"/> are values: they don't change, and compare by content.</summary>
public class InfoRecordTests
{
    private static ResourceInfo Version(Dictionary<string, string> metadata) => new()
    {
        ContainerName = "c", Name = "a", VersionId = "v1", Metadata = metadata
    };

    [Fact]
    public void ResourceInfoAndContainerInfo_ShouldBeEqual_WhenTheirMetadataHasTheSameEntries()
    {
        // Act: two reads of the same version and container, each with a dictionary of its own, in another order.
        ResourceInfo version = Version(new() { ["a"] = "1", ["b"] = "2" });
        ResourceInfo again = Version(new() { ["b"] = "2", ["a"] = "1" });
        var container = new ContainerInfo { Name = "c", Metadata = new Dictionary<string, string> { ["owner"] = "ops" } };
        var containerAgain = new ContainerInfo { Name = "c", Metadata = new Dictionary<string, string> { ["owner"] = "ops" } };

        // Assert
        Assert.Equal(version, again);
        Assert.Equal(version.GetHashCode(), again.GetHashCode());
        Assert.Equal(container, containerAgain);
        Assert.Equal(container.GetHashCode(), containerAgain.GetHashCode());
    }

    [Theory]
    [InlineData("b", "2")]
    [InlineData("a", "other")]
    [InlineData("A", "1")]
    public void ResourceInfoAndContainerInfo_ShouldNotBeEqual_WhenTheirMetadataDiffers(string key, string value)
    {
        // Act
        ResourceInfo version = Version(new() { ["a"] = "1" });
        ResourceInfo other = Version(new() { [key] = value });
        var container = new ContainerInfo { Name = "c", Metadata = new Dictionary<string, string> { ["a"] = "1" } };
        var otherContainer = new ContainerInfo { Name = "c", Metadata = new Dictionary<string, string> { [key] = value } };

        // Assert
        Assert.NotEqual(version, other);
        Assert.NotEqual(container, otherContainer);
    }

    [Fact]
    public void ResourceInfoAndContainerInfo_ShouldKeepTheirMetadata_WhenTheDictionaryTheyWereGivenChanges()
    {
        // Arrange
        var given = new Dictionary<string, string> { ["owner"] = "ops" };
        ResourceInfo version = Version(given);
        var container = new ContainerInfo { Name = "c", Metadata = given };

        // Act
        given["owner"] = "changed";

        // Assert: neither changed, and neither hands out a dictionary that can be changed.
        Assert.Equal(("ops", "ops"), (version.Metadata["owner"], container.Metadata["owner"]));
        Assert.IsNotAssignableFrom<IDictionary<string, string>>(version.Metadata);
        Assert.IsNotAssignableFrom<IDictionary<string, string>>(container.Metadata);
    }
}
