using Resourcefull.Models;

namespace Resourcefull.UnitTests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        var resource = new Resource();
        resource.Name = "Test";
        Assert.True(resource.Name == "Test");
    }
}