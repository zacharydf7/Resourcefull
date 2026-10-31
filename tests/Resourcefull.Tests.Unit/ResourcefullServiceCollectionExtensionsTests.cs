using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Resourcefull.InMemory;

namespace Resourcefull.Tests.Unit;

public class ResourcefullServiceCollectionExtensionsTests
{
    [Fact]
    public void AddResourcefull_ShouldThrowAnException_WhenNoProviderIsConfigured()
    {
        // Arrange & Act
        var services = new ServiceCollection();

        // Assert
        Assert.Throws<InvalidOperationException>(() => services.AddResourcefull(options => { }));
    }

    /// <summary>Services configured from <paramref name="json"/>, as appsettings.json would give it.</summary>
    private static ServiceProvider ConfiguredFrom(string json)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        return new ServiceCollection()
            .AddResourcefull(o => o.UseInMemory().UseConfiguration(configuration.GetSection("Resourcefull")))
            .BuildServiceProvider();
    }

    [Fact]
    public void UseConfiguration_ShouldTurnOnAndSetEachFeature_WhenGivenEverySection()
    {
        // Act
        using ServiceProvider provider = ConfiguredFrom("""
            {
              "Resourcefull": {
                "Versioning": { "MaxVersions": 10 },
                "Expiry": { "Interval": "00:00:30" },
                "Telemetry": { "RecordResourceNames": true },
                "ContainerPolicies": {
                  "Defaults": {
                    "MaxResourceSize": "100 MB", "MaxContainerSize": "5 GB", "MaxResourceCount": 1000,
                    "AllowedContentTypes": [ "application/pdf", "image/png" ], "ReadOnly": false
                  },
                  "CacheFor": "00:00:10",
                  "KeepLastKnownFor": "00:01:00"
                }
              }
            }
            """);

        // Assert
        ResourcefullOptions options = provider.GetRequiredService<IOptions<ResourcefullOptions>>().Value;
        Assert.Equal((true, 10, true),
            (options.IsVersioningEnabled, options.MaxVersions, options.Telemetry.RecordResourceNames));
        Assert.Equal(TimeSpan.FromSeconds(30), provider.GetRequiredService<ExpirySettings>().Interval);
        var policies = provider.GetRequiredService<ContainerPolicySettings>();
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)),
            (policies.CacheFor, policies.KeepLastKnownFor));
        Assert.Equivalent(new ResourceContainerPolicy
        {
            MaxResourceSize = ResourceSize.Megabytes(100), MaxContainerSize = ResourceSize.Gigabytes(5),
            MaxResourceCount = 1000, AllowedContentTypes = ["application/pdf", "image/png"], ReadOnly = false
        }, policies.Defaults);
    }

    [Fact]
    public void UseConfiguration_ShouldTurnOnEachFeatureWithItsDefaults_WhenGivenItsSectionEmpty()
    {
        // Act
        using ServiceProvider provider = ConfiguredFrom(
            """{ "Resourcefull": { "Versioning": {}, "Expiry": {}, "ContainerPolicies": {} } }""");

        // Assert
        ResourcefullOptions options = provider.GetRequiredService<IOptions<ResourcefullOptions>>().Value;
        Assert.Equal((true, 100), (options.IsVersioningEnabled, options.MaxVersions));
        Assert.Equal(TimeSpan.FromMinutes(1), provider.GetRequiredService<ExpirySettings>().Interval);
        Assert.Equivalent(new ResourceContainerPolicy(),
            provider.GetRequiredService<ContainerPolicySettings>().Defaults);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void UseConfiguration_ShouldAllowAnyContentType_WhenGivenAllowedContentTypesEmptyOrNull(string types)
    {
        // Act: the two read the same, and refusing every type would refuse every upload.
        using ServiceProvider provider = ConfiguredFrom($$"""
            { "Resourcefull": { "ContainerPolicies": { "Defaults": { "AllowedContentTypes": {{types}} } } } }
            """);

        // Assert
        Assert.Null(provider.GetRequiredService<ContainerPolicySettings>().Defaults.AllowedContentTypes);
    }

    [Fact]
    public void UseConfiguration_ShouldLeaveEveryFeatureOff_WhenGivenNoSections()
    {
        // Act
        using ServiceProvider provider = ConfiguredFrom("""{ "Resourcefull": {} }""");

        // Assert
        Assert.False(provider.GetRequiredService<IOptions<ResourcefullOptions>>().Value.IsVersioningEnabled);
        Assert.Null(provider.GetService<ExpirySettings>());
        Assert.Null(provider.GetService<ContainerPolicySettings>());
    }

    [Theory]
    [InlineData("""{ "Versioning": { "MaxVersion": 2 } }""",
        "Resourcefull:Versioning:MaxVersion isn't a Resourcefull setting")]
    [InlineData("""{ "Expirey": { "Interval": "00:01:00" } }""", "Resourcefull:Expirey isn't a Resourcefull setting")]
    [InlineData("""{ "ContainerPolicies": { "Defaults": { "MaxSize": "5 GB" } } }""",
        "Resourcefull:ContainerPolicies:Defaults:MaxSize isn't a Resourcefull setting")]
    [InlineData("""{ "Versioning": false }""", "Resourcefull:Versioning must be a section")]
    [InlineData("""{ "ContainerPolicies": { "Defaults": "x" } }""",
        "Resourcefull:ContainerPolicies:Defaults must be a section")]
    [InlineData("""{ "ContainerPolicies": { "Defaults": { "AllowedContentTypes": "application/pdf" } } }""",
        "Resourcefull:ContainerPolicies:Defaults:AllowedContentTypes must be a list of text")]
    [InlineData("""{ "ContainerPolicies": { "Defaults": { "AllowedContentTypes": [ { "Type": "x" } ] } } }""",
        "Resourcefull:ContainerPolicies:Defaults:AllowedContentTypes must be a list of text")]
    [InlineData("""{ "Expiry": { "Interval": "soon" } }""", "Resourcefull:Expiry:Interval")]
    [InlineData("""{ "Versioning": { "MaxVersions": 0 } }""", "Resourcefull:Versioning aren't valid")]
    [InlineData("""{ "Expiry": { "Interval": "00:00:00" } }""", "Resourcefull:Expiry aren't valid")]
    [InlineData("""{ "ContainerPolicies": { "CacheFor": "-00:00:01" } }""",
        "Resourcefull:ContainerPolicies aren't valid")]
    public void UseConfiguration_ShouldThrowNamingTheSetting_WhenGivenOneItCantUse(string section, string named)
    {
        // Act
        void act() => ConfiguredFrom($"{{ \"Resourcefull\": {section} }}").Dispose();

        // Assert
        Assert.Contains(named, Assert.Throws<InvalidOperationException>(act).Message);
    }

    [Fact]
    public void AddResourcefull_ShouldAddResourceServiceClient_WhenProviderIsConfigured()
    {
        // Arrange
        var services = new ServiceCollection();

        // Mock
        var resourcefullOptionsBuilderAction = new Mock<Action<ResourcefullOptionsBuilder>>();
        resourcefullOptionsBuilderAction.Setup(action => action.Invoke(It.IsAny<ResourcefullOptionsBuilder>()))
            .Callback((ResourcefullOptionsBuilder options) => options.UseInMemory());

        // Act
        services.AddResourcefull(resourcefullOptionsBuilderAction.Object);

        // Assert
        Assert.NotNull(services.SingleOrDefault(x => x.ServiceType == typeof(IResourceServiceClient)));
    }

    [Fact]
    public async Task AddResourcefull_ShouldLogThroughTheHostsLogging_WhenPruningFailsAfterAnUpload()
    {
        // Arrange: a store that keeps two versions of the name and can't delete the older one.
        var failure = new InvalidOperationException("storage unavailable");
        var repository = new Mock<IResourceRepository>();
        repository.Setup(r => r.AddResourceAsync("reports", It.IsAny<ResourceInfo>(), It.IsAny<Stream>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(new WrittenContent(0, "sha256:empty", DateTimeOffset.UnixEpoch));
        repository.Setup(r => r.GetResourceVersionsAsync("reports", "name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new() { ContainerName = "reports", Name = "name", VersionId = "new" },
                new() { ContainerName = "reports", Name = "name", VersionId = "old" }
            ]);
        repository.Setup(r => r.DeleteResourceAsync("reports", "name", "old", It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var logger = new Mock<ILogger>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var loggerProvider = new Mock<ILoggerProvider>();
        loggerProvider.Setup(p => p.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        loggerProvider.Setup(p => p.CreateLogger("Resourcefull.ResourceContainerClient")).Returns(logger.Object);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(loggerProvider.Object));
        services.AddResourcefull(options => options.UseInMemory());
        services.AddScoped(_ => repository.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IResourceServiceClient>().GetResourceContainerClient("reports");

        // Act
        await client.UploadAsync("name", new MemoryStream(), new ResourceUploadOptions { Overwrite = true });

        // Assert
        logger.Verify(l => l.Log(LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), failure,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}