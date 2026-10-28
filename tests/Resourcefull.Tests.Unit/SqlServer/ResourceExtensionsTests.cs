using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Models;

namespace Resourcefull.Tests.Unit.SqlServer;
public class ResourceExtensionsTests
{
    [Fact]
    public void ToResourceInfo_ShouldCopyEveryField_WhenModelIsPopulated()
    {
        // Arrange
        var containerName = "containerName";
        var resource = new ResourceModel { ContainerName = containerName, Name = "test", ContentLength = 100, ContentType = "Application/PDF", Uri = $"http://test.com/api/resources/{containerName}" };
        resource.Metadata.Add("someKey", "someValue");

        // Act
        var resourceInfo = resource.ToResourceInfo();

        // Assert
        Assert.Equal(resource.ContainerName, resourceInfo.ContainerName);
        Assert.Equal(resource.Name, resourceInfo.Name);
        Assert.Equal(resource.ContentLength, resourceInfo.ContentLength);
        Assert.Equal(resource.ContentType, resourceInfo.ContentType);
        Assert.Equal(resource.Uri, resourceInfo.Uri);
        Assert.Equal(resource.Metadata, resourceInfo.Metadata);

    }
}