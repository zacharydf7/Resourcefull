using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit;

public class ContainerPolicyTests
{
    [Theory]
    [InlineData("5 GB", 5L << 30)]
    [InlineData("100MB", 100L << 20)]
    [InlineData("1.5 gb", 3L << 29)]
    [InlineData("512 KiB", 512L << 10)]
    [InlineData(" 2048 ", 2048L)]
    [InlineData("7 B", 7L)]
    [InlineData("0.1 KB", 102L)]
    public void Parse_ShouldReadTheSizeIn1024BasedUnits_WhenGivenAValidSize(string text, long bytes)
    {
        // Act
        ResourceSize size = ResourceSize.Parse(text);

        // Assert
        Assert.Equal(bytes, size.Bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GB")]
    [InlineData("-5 GB")]
    [InlineData("5 PB")]
    [InlineData("5,5 GB")]
    [InlineData("99999999999 TB")]
    public void TryParse_ShouldReturnFalse_WhenGivenSomethingOtherThanASize(string text)
    {
        // Act
        bool parsed = ResourceSize.TryParse(text, null, out _);

        // Assert
        Assert.False(parsed);
        Assert.Throws<FormatException>(() => ResourceSize.Parse(text));
    }

    [Theory]
    [InlineData(5L << 30, "5 GB")]
    [InlineData(3L << 29, "1536 MB")]
    [InlineData(1500L, "1500 B")]
    [InlineData(0L, "0 B")]
    public void ToString_ShouldUseTheLargestWholeUnit_WhenCalled(long bytes, string text)
    {
        // Act & Assert
        Assert.Equal(text, new ResourceSize(bytes).ToString());
        Assert.Equal(bytes, ResourceSize.Parse(text).Bytes);
    }

    [Fact]
    public void Factories_ShouldMultiplyBy1024PerUnit_WhenCalled()
    {
        // Act & Assert
        Assert.Equal(1L << 10, ResourceSize.Kilobytes(1).Bytes);
        Assert.Equal(100L << 20, ResourceSize.Megabytes(100).Bytes);
        Assert.Equal(5L << 30, ResourceSize.Gigabytes(5).Bytes);
        Assert.Equal(2L << 40, ResourceSize.Terabytes(2).Bytes);
        Assert.Equal(42, ((ResourceSize)42).Bytes);
        Assert.Throws<OverflowException>(() => ResourceSize.Terabytes(long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceSize(-1));
    }

    [Fact]
    public void Bind_ShouldReadAPolicyFromConfiguration_WhenSizesAreWrittenWithUnits()
    {
        // Arrange
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Policy:MaxResourceSize"] = "100 MB",
            ["Policy:MaxContainerSize"] = "5 GB",
            ["Policy:MaxResourceCount"] = "1000",
            ["Policy:AllowedContentTypes:0"] = "image/*",
            ["Policy:ReadOnly"] = "false"
        }).Build();

        // Act
        ResourceContainerPolicy policy = configuration.GetSection("Policy").Get<ResourceContainerPolicy>()!;

        // Assert
        Assert.Equal(ResourceSize.Megabytes(100), policy.MaxResourceSize);
        Assert.Equal(ResourceSize.Gigabytes(5), policy.MaxContainerSize);
        Assert.Equal(1000, policy.MaxResourceCount);
        Assert.Equal(["image/*"], policy.AllowedContentTypes);
        Assert.False(policy.ReadOnly);
    }

    [Fact]
    public void LimitedContentStream_ShouldCountFromWhereItIsRewound_WhenAProviderRetriesTheUpload()
    {
        // Arrange: exactly the limit, read once, then again after a rewind, as a retry reads it.
        var limit = new ContentLimit(100, ResourcePolicyRule.MaxResourceSize, 100, "too large");
        using var stream = new LimitedContentStream(new MemoryStream(new byte[100]), limit);
        stream.CopyTo(Stream.Null);

        // Act
        stream.Position = 0;
        Exception? second = Record.Exception(() => stream.CopyTo(Stream.Null));

        // Assert
        Assert.Null(second);
    }

    [Fact]
    public void LimitedContentStream_ShouldThrowTheRulesViolation_WhenMoreThanTheLimitIsRead()
    {
        // Arrange
        var limit = new ContentLimit(10, ResourcePolicyRule.MaxContainerSize, 500, "no room");
        using var stream = new LimitedContentStream(new MemoryStream(new byte[11]), limit);

        // Act
        var violation = Assert.Throws<ResourcePolicyViolationException>(() => stream.CopyTo(Stream.Null));

        // Assert
        Assert.Equal((ResourcePolicyRule.MaxContainerSize, 500L, "no room"), (violation.Rule, violation.Limit, violation.Message));
    }

    [Fact]
    public void UseContainerPolicies_ShouldThrow_WhenGivenANegativeCacheTime()
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseContainerPolicies(cacheFor: TimeSpan.FromSeconds(-1)));
    }

    private sealed class ReadOnlyProvider : IResourcePolicyProvider
    {
        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ResourceContainerPolicy?>(new ResourceContainerPolicy { ReadOnly = true });
    }

    [Fact]
    public async Task UsePolicyProvider_ShouldTurnOnPolicies_WhenUseContainerPoliciesIsNotCalled()
    {
        // Arrange
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UsePolicyProvider<ReadOnlyProvider>()).BuildServiceProvider();
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();

        // Act
        Task act() => service.DeleteResourceContainerIfExistsAsync("c");

        // Assert
        Assert.Equal(ResourcePolicyRule.ReadOnly, (await Assert.ThrowsAsync<ResourcePolicyViolationException>(act)).Rule);
    }

    [Fact]
    public async Task GetContainerUsageAsync_ShouldAddUpTheVersionsListing_WhenAProviderWrittenFor31IsAsked()
    {
        // Arrange
        var repository = new Mock<IResourceRepository> { CallBase = true };
        repository.Setup(r => r.GetAllResourceInfoAsync("c", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceInfo { ContainerName = "c", Name = "a" }, new ResourceInfo { ContainerName = "c", Name = "b" }]);
        repository.Setup(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceInfo { ContainerName = "c", Name = "a", ContentLength = 10 }, new ResourceInfo { ContainerName = "c", Name = "a", ContentLength = 5 }]);
        repository.Setup(r => r.GetResourceVersionsAsync("c", "b", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceInfo { ContainerName = "c", Name = "b", ContentLength = null }]);

        // Act
        ResourceContainerUsage usage = await repository.Object.GetContainerUsageAsync("c");

        // Assert
        Assert.Equal(new ResourceContainerUsage(15, 2, 3), usage);
    }

    [Fact]
    public async Task GetUsageAsync_ShouldThrowNotSupported_WhenAClientWrittenFor31IsAsked()
    {
        // Arrange
        IResourceContainerClient client = new Mock<IResourceContainerClient> { CallBase = true }.Object;

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetUsageAsync());
    }
}
