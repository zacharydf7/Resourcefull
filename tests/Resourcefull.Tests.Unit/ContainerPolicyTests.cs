using System.Collections.Concurrent;
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

    private sealed class LargerLimitsProvider : IResourcePolicyProvider
    {
        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ResourceContainerPolicy?>(new ResourceContainerPolicy
            {
                MaxResourceSize = ResourceSize.Gigabytes(1), MaxContainerSize = ResourceSize.Gigabytes(5)
            });
    }

    [Fact]
    public async Task GetPolicyAsync_ShouldGiveTheProvidersLimitsInPlaceOfTheDefaults_WhenBothSetSome()
    {
        // Arrange: defaults for the size and types, and a provider that sets the size and the container's size.
        using ServiceProvider provider = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory()
                .UseContainerPolicies(new ResourceContainerPolicy
                {
                    MaxResourceSize = ResourceSize.Megabytes(100), AllowedContentTypes = ["application/pdf"]
                })
                .UsePolicyProvider<LargerLimitsProvider>())
            .BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateResourceContainerAsync("c");

        // Act
        ResourceContainerPolicy policy = await container.GetPolicyAsync();

        // Assert
        Assert.Equal((ResourceSize.Gigabytes(1), ResourceSize.Gigabytes(5)),
            (policy.MaxResourceSize, policy.MaxContainerSize));
        Assert.Equal(["application/pdf"], policy.AllowedContentTypes!);
        Assert.Null(policy.MaxResourceCount);
    }

    [Fact]
    public async Task GetPolicyAsync_ShouldGiveNoLimits_WhenContainerPoliciesAreOff()
    {
        // Arrange
        using ServiceProvider provider =
            new ServiceCollection().AddResourcefull(o => o.UseInMemory()).BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateResourceContainerAsync("c");

        // Act
        ResourceContainerPolicy policy = await container.GetPolicyAsync();

        // Assert
        Assert.Equivalent(new ResourceContainerPolicy(), policy);
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

    private static ResourceInfo Version(string id, long length) => new() { ContainerName = "c", Name = "a", VersionId = id, ContentLength = length };

    /// <summary>A versioned client of container <c>c</c> whose policies come from <paramref name="settings"/>.</summary>
    private static ResourceContainerClient NewMovingClient(IResourceRepository repository, ContainerPolicySettings settings) =>
        new(repository, null, new ResourcefullOptions { IsVersioningEnabled = true }, new ResourceContainerInfo { Name = "c" }, null,
            new ContainerPolicies(settings, repository));

    [Fact]
    public async Task MoveResourceAsync_ShouldCheckTheNewCurrentVersion_WhenAnUploadMakesOneCurrentDuringTheMove()
    {
        // Arrange: the move checks v1, then an upload makes v2, too large for the destination, current before it moves.
        using var settings = new ContainerPolicySettings(new ResourceContainerPolicy { MaxResourceSize = new ResourceSize(10) }, TimeSpan.Zero, TimeSpan.Zero);
        var repository = new Mock<IResourceRepository>();
        repository.SetupSequence(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Version("v1", 5)])
            .ReturnsAsync([Version("v2", 50), Version("v1", 5)]);
        repository.Setup(r => r.MoveResourceAsync(new ResourceLocation("c", "a"), new ResourceLocation("d", "a"),
                It.Is<ResourceConditions>(c => c.IfMatchVersionId == "v1"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourcePreconditionFailedException("v2 is current.", "v2", null));

        // Act
        Task act() => NewMovingClient(repository.Object, settings).MoveResourceAsync("a", "d", "a");

        // Assert
        ResourcePolicyViolationException refused = await Assert.ThrowsAsync<ResourcePolicyViolationException>(act);
        Assert.Equal(ResourcePolicyRule.MaxResourceSize, refused.Rule);
        repository.Verify(r => r.MoveResourceAsync(It.IsAny<ResourceLocation>(), It.IsAny<ResourceLocation>(),
            It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldReportTheCallersUnmetConditions_WhenTheCurrentVersionHasNotChanged()
    {
        // Arrange
        using var settings = new ContainerPolicySettings(new ResourceContainerPolicy(), TimeSpan.Zero, TimeSpan.Zero);
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>())).ReturnsAsync([Version("v1", 5)]);
        repository.Setup(r => r.MoveResourceAsync(new ResourceLocation("c", "a"), new ResourceLocation("d", "a"),
                It.Is<ResourceConditions>(c => c.IfMatchVersionId == "v1" && c.IfMatchContentHash == "sha256:other"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourcePreconditionFailedException("The hash doesn't match.", "v1", "sha256:stored"));

        // Act
        Task act() => NewMovingClient(repository.Object, settings).MoveResourceAsync("a", "d", "a",
            new ResourceMoveOptions { SourceConditions = new ResourceConditions { IfMatchContentHash = "sha256:other" } });

        // Assert
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        repository.Verify(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldMoveTheNewCurrentVersion_WhenItMeetsThePolicyOnTheSecondCheck()
    {
        // Arrange: an upload makes v2 current between the first check and the move; v2 fits too.
        using var settings = new ContainerPolicySettings(new ResourceContainerPolicy { MaxResourceSize = new ResourceSize(10) }, TimeSpan.Zero, TimeSpan.Zero);
        var repository = new Mock<IResourceRepository>();
        repository.SetupSequence(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([Version("v1", 5)])
            .ReturnsAsync([Version("v2", 6), Version("v1", 5)]);
        repository.Setup(r => r.MoveResourceAsync(new ResourceLocation("c", "a"), new ResourceLocation("d", "a"),
                It.Is<ResourceConditions>(c => c.IfMatchVersionId == "v1"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ResourcePreconditionFailedException("v2 is current.", "v2", null));
        repository.Setup(r => r.MoveResourceAsync(new ResourceLocation("c", "a"), new ResourceLocation("d", "a"),
                It.Is<ResourceConditions>(c => c.IfMatchVersionId == "v2"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repository.Setup(r => r.GetResourceInfoAsync("d", "a", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceInfo { ContainerName = "d", Name = "a", VersionId = "v2" });

        // Act
        ResourceInfo? moved = await NewMovingClient(repository.Object, settings).MoveResourceAsync("a", "d", "a");

        // Assert
        Assert.Equal(("d", "v2"), (moved!.ContainerName, moved.VersionId));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldGiveUpAfterFiveChecks_WhenUploadsKeepMakingNewVersionsCurrent()
    {
        // Arrange: every time the move checks the name, another upload has made a newer version current by the move.
        using var settings = new ContainerPolicySettings(new ResourceContainerPolicy(), TimeSpan.Zero, TimeSpan.Zero);
        var repository = new Mock<IResourceRepository>();
        int checks = 0;
        repository.Setup(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => [Version($"v{++checks}", 5)]);
        repository.Setup(r => r.MoveResourceAsync(new ResourceLocation("c", "a"), new ResourceLocation("d", "a"),
                It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromException<bool>(new ResourcePreconditionFailedException("A newer version is current.", $"v{checks + 1}", null)));

        // Act
        Task act() => NewMovingClient(repository.Object, settings).MoveResourceAsync("a", "d", "a");

        // Assert
        await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Equal(5, checks);
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldReportTheCallersVersionUnmetWithoutMoving_WhenItIsNotTheCurrentOne()
    {
        // Arrange
        using var settings = new ContainerPolicySettings(new ResourceContainerPolicy(), TimeSpan.Zero, TimeSpan.Zero);
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.GetResourceVersionsAsync("c", "a", It.IsAny<CancellationToken>())).ReturnsAsync([Version("v1", 5)]);

        // Act
        Task act() => NewMovingClient(repository.Object, settings).MoveResourceAsync("a", "d", "a",
            new ResourceMoveOptions { SourceConditions = new ResourceConditions { IfMatchVersionId = "v0" } });

        // Assert
        ResourcePreconditionFailedException failed = await Assert.ThrowsAsync<ResourcePreconditionFailedException>(act);
        Assert.Equal("v1", failed.CurrentVersionId);
        repository.Verify(r => r.MoveResourceAsync(It.IsAny<ResourceLocation>(), It.IsAny<ResourceLocation>(),
            It.IsAny<ResourceConditions?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Allows files of up to 10 bytes, except for containers whose policy store is down.</summary>
    private sealed class FlakyPolicies : IResourcePolicyProvider
    {
        public static readonly ConcurrentDictionary<string, bool> Down = new();

        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            Down.ContainsKey(containerName)
                ? throw new InvalidOperationException("The policy store is down.")
                : ValueTask.FromResult<ResourceContainerPolicy?>(new ResourceContainerPolicy { MaxResourceSize = new ResourceSize(10) });
    }

    private static async Task<IResourceContainerClient> NewFlakyContainerAsync(ServiceProvider services) =>
        await services.GetRequiredService<IResourceServiceClient>().CreateResourceContainerAsync($"c-{Guid.NewGuid():N}");

    [Fact]
    public async Task UploadAsync_ShouldCheckTheLastKnownPolicy_WhenThePolicyProviderFails()
    {
        // Arrange: the provider gave the container's policy once, then its store went down.
        using ServiceProvider services = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseContainerPolicies(cacheFor: TimeSpan.Zero).UsePolicyProvider<FlakyPolicies>())
            .BuildServiceProvider();
        IResourceContainerClient container = await NewFlakyContainerAsync(services);
        await container.UploadAsync("small", BinaryData.FromString("12345"));
        FlakyPolicies.Down[container.ResourceContainerInfo.Name] = true;

        // Act
        Task tooLarge() => container.UploadAsync("large", new BinaryData(new byte[50]));
        ResourceInfo stillAllowed = await container.UploadAsync("small2", BinaryData.FromString("12345"));

        // Assert
        Assert.Equal(ResourcePolicyRule.MaxResourceSize, (await Assert.ThrowsAsync<ResourcePolicyViolationException>(tooLarge)).Rule);
        Assert.Equal(5, stillAllowed.ContentLength);
    }

    [Theory]
    [InlineData(false, null)]   // the provider never gave the container's policy
    [InlineData(true, 0)]       // it did, but nothing is kept
    public async Task UploadAsync_ShouldThrowThePolicyProvidersException_WhenNoLastKnownPolicyIsKept(bool givenBefore, int? keepSeconds)
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory()
                .UseContainerPolicies(cacheFor: TimeSpan.Zero, keepLastKnownFor: keepSeconds is { } s ? TimeSpan.FromSeconds(s) : null)
                .UsePolicyProvider<FlakyPolicies>())
            .BuildServiceProvider();
        IResourceContainerClient container = await NewFlakyContainerAsync(services);
        if (givenBefore) await container.UploadAsync("small", BinaryData.FromString("12345"));
        FlakyPolicies.Down[container.ResourceContainerInfo.Name] = true;

        // Act
        Task act() => container.UploadAsync("small2", BinaryData.FromString("12345"));

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task UploadAsync_ShouldThrowThePolicyProvidersException_WhenTheLastKnownPolicyIsOlderThanKeepLastKnownFor()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory()
                .UseContainerPolicies(cacheFor: TimeSpan.Zero, keepLastKnownFor: TimeSpan.FromMilliseconds(50))
                .UsePolicyProvider<FlakyPolicies>())
            .BuildServiceProvider();
        IResourceContainerClient container = await NewFlakyContainerAsync(services);
        await container.UploadAsync("small", BinaryData.FromString("12345"));
        FlakyPolicies.Down[container.ResourceContainerInfo.Name] = true;
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // Act
        Task act() => container.UploadAsync("small2", BinaryData.FromString("12345"));

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    /// <summary>Gives a container's policy once, then fails, counting how often it's asked.</summary>
    private sealed class CountingFailingPolicies : IResourcePolicyProvider
    {
        public static readonly ConcurrentDictionary<string, int> Asked = new();

        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            Asked.AddOrUpdate(containerName, 1, (_, n) => n + 1) == 1
                ? ValueTask.FromResult<ResourceContainerPolicy?>(null)
                : throw new InvalidOperationException("The policy store is down.");
    }

    [Fact]
    public async Task ThrowIfReadOnlyAsync_ShouldAskTheFailingProviderOncePerCacheTime_WhenTheLastKnownPolicyStandsIn()
    {
        // Arrange: the provider gave the policy, then its cached answer ran out, and the provider went down.
        using var settings = new ContainerPolicySettings(new ResourceContainerPolicy(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        var policies = new ContainerPolicies(settings, new Mock<IResourceRepository>().Object, new CountingFailingPolicies());
        var telemetry = new ClientTelemetry("test", new ResourcefullOptions());
        string name = $"c-{Guid.NewGuid():N}";
        await policies.ThrowIfReadOnlyAsync(name, telemetry, CancellationToken.None);
        settings.Cache.Remove(name);

        // Act: three writes during the outage, within one cache time of each other.
        for (int i = 0; i < 3; i++) await policies.ThrowIfReadOnlyAsync(name, telemetry, CancellationToken.None);

        // Assert: the first fell back to the last known policy, which the next two then found cached.
        Assert.Equal(2, CountingFailingPolicies.Asked[name]);
    }

    /// <summary>Gives a container's policy once, then fails as a caller's canceled token makes it.</summary>
    private sealed class CanceledPolicies : IResourcePolicyProvider
    {
        private static readonly ConcurrentDictionary<string, bool> Given = new();

        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken)
        {
            if (Given.TryAdd(containerName, true)) return ValueTask.FromResult<ResourceContainerPolicy?>(null);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected a canceled token.");
        }
    }

    [Fact]
    public async Task ThrowIfReadOnlyAsync_ShouldThrowOperationCanceled_WhenTheCallerCancelsWhileThePolicyProviderFails()
    {
        // Arrange: a last known policy is kept, but the failure is the caller's own cancellation.
        using var settings = new ContainerPolicySettings(new ResourceContainerPolicy(), TimeSpan.Zero, TimeSpan.FromMinutes(5));
        var policies = new ContainerPolicies(settings, new Mock<IResourceRepository>().Object, new CanceledPolicies());
        var telemetry = new ClientTelemetry("test", new ResourcefullOptions());
        string name = $"c-{Guid.NewGuid():N}";
        await policies.ThrowIfReadOnlyAsync(name, telemetry, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        // Act
        Task act() => policies.ThrowIfReadOnlyAsync(name, telemetry, canceled.Token);

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    [Fact]
    public void UseContainerPolicies_ShouldThrowArgumentOutOfRange_WhenKeepLastKnownForIsNegative()
    {
        // Arrange
        var builder = new ResourcefullOptionsBuilder(new ServiceCollection());

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.UseContainerPolicies(keepLastKnownFor: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task PruneContainerAsync_ShouldThrowPolicyViolation_WhenTheContainerIsReadOnly()
    {
        // Arrange
        using ServiceProvider services = new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseContainerPolicies(new ResourceContainerPolicy { ReadOnly = true }))
            .BuildServiceProvider();
        IResourceServiceClient service = services.GetRequiredService<IResourceServiceClient>();
        await service.CreateResourceContainerAsync("c");

        // Act
        Task act() => service.PruneContainerAsync("c");

        // Assert
        Assert.Equal(ResourcePolicyRule.ReadOnly, (await Assert.ThrowsAsync<ResourcePolicyViolationException>(act)).Rule);
    }
}
