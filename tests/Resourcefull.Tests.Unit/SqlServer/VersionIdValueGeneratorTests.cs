using Microsoft.EntityFrameworkCore;
using Resourcefull.SqlServer;
using Resourcefull.SqlServer.Models;
using Resourcefull.SqlServer.ValueGenerators;

namespace Resourcefull.Tests.Unit.SqlServer;

public class VersionIdValueGeneratorTests
{
    private static ResourceDbContext NewContext()
    {
        DbContextOptions<ResourceDbContext> options = new DbContextOptionsBuilder<ResourceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ResourceDbContext(options);
    }

    [Fact]
    public void Next_ShouldReturnExistingVersionId_WhenAlreadySet()
    {
        // Arrange
        using var context = NewContext();
        var resource = new ResourceModel { ContainerName = "c", Name = "n", VersionId = "explicit-version" };
        var generator = new VersionIdValueGenerator();

        // Act
        string generated = generator.Next(context.Entry(resource));

        // Assert
        Assert.Equal("explicit-version", generated);
    }

    [Fact]
    public void Next_ShouldGenerateParseableTimestamp_WhenVersionIdIsNull()
    {
        // Arrange
        using var context = NewContext();
        var resource = new ResourceModel { ContainerName = "c", Name = "n", VersionId = null };
        var generator = new VersionIdValueGenerator();

        // Act
        string generated = generator.Next(context.Entry(resource));

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(generated));
        Assert.True(DateTime.TryParse(generated, out _));
    }

    [Fact]
    public void GeneratesTemporaryValues_ShouldBeFalse_WhenGeneratorIsConstructed()
    {
        // Arrange & Act
        var generator = new VersionIdValueGenerator();

        // Assert
        Assert.False(generator.GeneratesTemporaryValues);
    }
}
