using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.Contract;

public abstract partial class ResourcefullContractTests
{
    /// <summary>A policy provider whose answers each test sets for its own containers.</summary>
    private sealed class TestPolicyProvider : IResourcePolicyProvider
    {
        public static readonly ConcurrentDictionary<string, ResourceContainerPolicy> Policies = new();

        public ValueTask<ResourceContainerPolicy?> GetPolicyAsync(string containerName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Policies.GetValueOrDefault(containerName));
    }

    private static ResourceUploadOptions Typed(string contentType, long? length = null) =>
        new() { Headers = new ResourceHeaders(contentType, length) };

    /// <summary>Content that throws if it's read, for a write that must be refused before its content is.</summary>
    private sealed class UnreadableStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("The content was read.");

        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("The content was read.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The content was read.");
    }

    [SkippableFact]
    public async Task EveryWrite_ShouldBeRefusedAndChangeNothing_WhenTheContainerIsReadOnly()
    {
        // Arrange: made read-only after a version is stored, with no caching, so the change applies at once.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning()
            .UseContainerPolicies(cacheFor: TimeSpan.Zero).UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string name = NewContainerName();
        IResourceContainerClient frozen = await service.CreateResourceContainerAsync(name);
        ResourceInfo stored = await frozen.UploadAsync("notes.txt", BinaryData.FromString("kept"));
        TestPolicyProvider.Policies[name] = new ResourceContainerPolicy { ReadOnly = true };

        // Act
        var refused = new[]
        {
            await Record.ExceptionAsync(() => frozen.UploadAsync("other.txt", BinaryData.FromString("x"))),
            await Record.ExceptionAsync(() => frozen.RestoreVersionAsync("notes.txt", stored.VersionId)),
            await Record.ExceptionAsync(() => frozen.DeleteResourceIfExistsAsync("notes.txt")),
            await Record.ExceptionAsync(() => service.DeleteResourceContainerIfExistsAsync(name))
        };

        // Assert
        Assert.All(refused, e => Assert.Equal(ResourcePolicyRule.ReadOnly, Assert.IsType<ResourcePolicyViolationException>(e).Rule));
        Assert.Equal("kept", (await frozen.DownloadContentAsync("notes.txt"))!.Content.ToString());
        Assert.Single(await frozen.GetResourceVersionsAsync("notes.txt"));
        Assert.False(await frozen.ExistsAsync("other.txt"));
    }

    [SkippableTheory]
    [InlineData("image/png", true)]
    [InlineData("IMAGE/JPEG", true)]
    [InlineData("application/pdf; charset=binary", true)]
    [InlineData("text/html", false)]
    [InlineData("application/pdfx", false)]
    [InlineData(null, false)]
    [InlineData("image/png;,text/html", false)]                 // browsers read each of these as text/html
    [InlineData("image/png, text/html", false)]
    [InlineData("application/pdf;x=\",text/html", false)]
    public async Task UploadAsync_ShouldStoreOnlyAllowedContentTypes_WhenTheContainerAllowsSome(string? contentType, bool allowed)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(
            new ResourceContainerPolicy { AllowedContentTypes = ["image/*", "application/pdf"] }));
        IResourceContainerClient container = await NewContainerAsync(provider);

        // Act
        Exception? refused = await Record.ExceptionAsync(() => container.UploadAsync("file", BinaryData.FromString("x"),
            new ResourceUploadOptions { Headers = new ResourceHeaders(contentType, null) }));

        // Assert
        Assert.Equal(allowed, refused is null);
        Assert.Equal(allowed, await container.ExistsAsync("file"));
        if (!allowed) Assert.Equal(ResourcePolicyRule.AllowedContentTypes, Assert.IsType<ResourcePolicyViolationException>(refused).Rule);
    }

    [SkippableTheory]
    [InlineData(100L, true)]     // declared over the limit: refused before any content is read
    [InlineData(null, true)]     // undeclared: refused as the content passes the limit
    [InlineData(64L, false)]
    public async Task UploadAsync_ShouldRefuseAndStoreNothing_WhenTheContentIsLargerThanMaxResourceSize(long? contentLength, bool tooLarge)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseContainerPolicies(new ResourceContainerPolicy { MaxResourceSize = 64 }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        Stream content = tooLarge && contentLength is not null ? new UnreadableStream() : new MemoryStream(new byte[tooLarge ? 100 : 64]);

        // Act
        Exception? refused = await Record.ExceptionAsync(() => container.UploadAsync("file", content,
            new ResourceUploadOptions { Headers = new ResourceHeaders(null, contentLength) }));

        // Assert
        Assert.Equal(!tooLarge, await container.ExistsAsync("file"));
        if (tooLarge)
        {
            var violation = Assert.IsType<ResourcePolicyViolationException>(refused);
            Assert.Equal((ResourcePolicyRule.MaxResourceSize, 64L), (violation.Rule, violation.Limit));
        }
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldRefuseWhatDoesNotFit_WhenTheContainerHasAMaxContainerSize()
    {
        // Arrange: 100 bytes allowed, and 60 stored in two versions of one name.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning()
            .UseContainerPolicies(new ResourceContainerPolicy { MaxContainerSize = 100 }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", new MemoryStream(new byte[30]));
        await container.UploadAsync("a", new MemoryStream(new byte[30]));

        // Act
        Exception? declared = await Record.ExceptionAsync(() => container.UploadAsync("b", new UnreadableStream(),
            new ResourceUploadOptions { Headers = new ResourceHeaders(null, 50) }));
        Exception? undeclared = await Record.ExceptionAsync(() => container.UploadAsync("c", new MemoryStream(new byte[50])));
        ResourceInfo fits = await container.UploadAsync("d", new MemoryStream(new byte[40]));

        // Assert
        Assert.All(new[] { declared, undeclared }, e => Assert.Equal((ResourcePolicyRule.MaxContainerSize, 100L),
            (Assert.IsType<ResourcePolicyViolationException>(e).Rule, ((ResourcePolicyViolationException)e!).Limit)));
        Assert.False(await container.ExistsAsync("b"));
        Assert.False(await container.ExistsAsync("c"));
        Assert.Equal(40, fits.ContentLength);
        Assert.Equal(new ResourceContainerUsage(100, 2, 3), await container.GetUsageAsync());
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldRefuseANewNameButNotANewVersion_WhenTheContainerHoldsMaxResourceCount()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning()
            .UseContainerPolicies(new ResourceContainerPolicy { MaxResourceCount = 2 }));
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", BinaryData.FromString("1"));
        await container.UploadAsync("b", BinaryData.FromString("1"));

        // Act
        Exception? third = await Record.ExceptionAsync(() => container.UploadAsync("c", BinaryData.FromString("1")));
        await container.UploadAsync("a", BinaryData.FromString("2"));

        // Assert
        var violation = Assert.IsType<ResourcePolicyViolationException>(third);
        Assert.Equal((ResourcePolicyRule.MaxResourceCount, 2L), (violation.Rule, violation.Limit));
        Assert.False(await container.ExistsAsync("c"));
        Assert.Equal(2, (await container.GetResourceVersionsAsync("a")).Count);
    }

    [SkippableFact]
    public async Task RestoreVersionAsync_ShouldBeRefused_WhenTheVersionIsLargerThanMaxResourceSize()
    {
        // Arrange: a version stored before the limit was lowered, with no caching, so the change applies at once.
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning()
            .UseContainerPolicies(cacheFor: TimeSpan.Zero).UsePolicyProvider<TestPolicyProvider>());
        string name = NewContainerName();
        IResourceContainerClient container = await ServiceOf(provider).CreateResourceContainerAsync(name);
        ResourceInfo large = await container.UploadAsync("a", new MemoryStream(new byte[100]));
        await container.UploadAsync("a", new MemoryStream(new byte[10]));
        TestPolicyProvider.Policies[name] = new ResourceContainerPolicy { MaxResourceSize = 50 };

        // Act
        Task act() => container.RestoreVersionAsync("a", large.VersionId);

        // Assert
        Assert.Equal(ResourcePolicyRule.MaxResourceSize, (await Assert.ThrowsAsync<ResourcePolicyViolationException>(act)).Rule);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("a")).Count);
    }

    [SkippableFact]
    public async Task UploadAsync_ShouldApplyAContainersOwnPolicyOverTheDefaults_WhenTheProviderGivesOne()
    {
        // Arrange: the defaults allow only text, and limit size; one container's own policy allows images, and sets no
        // size, so the default size still applies to it.
        using ServiceProvider provider = BuildServiceProvider(o => o
            .UseContainerPolicies(new ResourceContainerPolicy { AllowedContentTypes = ["text/plain"], MaxResourceSize = 10 })
            .UsePolicyProvider<TestPolicyProvider>());
        IResourceServiceClient service = ServiceOf(provider);
        string images = NewContainerName();
        TestPolicyProvider.Policies[images] = new ResourceContainerPolicy { AllowedContentTypes = ["image/png"] };
        IResourceContainerClient own = await service.CreateResourceContainerAsync(images);
        IResourceContainerClient other = await NewContainerAsync(provider);

        // Act
        await own.UploadAsync("small.png", BinaryData.FromString("png"), Typed("image/png"));
        Exception? text = await Record.ExceptionAsync(() => own.UploadAsync("notes.txt", BinaryData.FromString("x"), Typed("text/plain")));
        Exception? large = await Record.ExceptionAsync(() => own.UploadAsync("large.png", new MemoryStream(new byte[11]), Typed("image/png")));
        Exception? png = await Record.ExceptionAsync(() => other.UploadAsync("small.png", BinaryData.FromString("png"), Typed("image/png")));

        // Assert
        Assert.Equal(ResourcePolicyRule.AllowedContentTypes, Assert.IsType<ResourcePolicyViolationException>(text).Rule);
        Assert.Equal(ResourcePolicyRule.MaxResourceSize, Assert.IsType<ResourcePolicyViolationException>(large).Rule);
        Assert.Equal(ResourcePolicyRule.AllowedContentTypes, Assert.IsType<ResourcePolicyViolationException>(png).Rule);
    }

    [SkippableTheory]
    [InlineData(0, true)]
    [InlineData(60, false)]
    public async Task UploadAsync_ShouldApplyAChangedPolicyOnlyOnceItsCacheTimeHasPassed_WhenTheProviderChangesIt(int cacheSeconds, bool appliesNow)
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o
            .UseContainerPolicies(cacheFor: TimeSpan.FromSeconds(cacheSeconds))
            .UsePolicyProvider<TestPolicyProvider>());
        string name = NewContainerName();
        IResourceContainerClient container = await ServiceOf(provider).CreateResourceContainerAsync(name);
        await container.UploadAsync("first", BinaryData.FromString("x"));

        // Act
        TestPolicyProvider.Policies[name] = new ResourceContainerPolicy { ReadOnly = true };
        Exception? second = await Record.ExceptionAsync(() => container.UploadAsync("second", BinaryData.FromString("x")));

        // Assert
        Assert.Equal(appliesNow, second is ResourcePolicyViolationException);
    }

    [SkippableFact]
    public async Task GetUsageAsync_ShouldCountEveryVersionsSizeAndTheNames_WhenCalled()
    {
        // Arrange
        using ServiceProvider provider = BuildServiceProvider(o => o.UseVersioning());
        IResourceContainerClient container = await NewContainerAsync(provider);
        await container.UploadAsync("a", new MemoryStream(new byte[100_000]));
        await container.UploadAsync("a", new MemoryStream(new byte[5]));
        await container.UploadAsync("b", new MemoryStream(new byte[7]));

        // Act
        ResourceContainerUsage usage = await container.GetUsageAsync();
        ResourceContainerUsage empty = await ServiceOf(provider).GetResourceContainerClient(NewContainerName()).GetUsageAsync();

        // Assert
        Assert.Equal(new ResourceContainerUsage(100_012, 2, 3), usage);
        Assert.Equal(new ResourceContainerUsage(0, 0, 0), empty);
    }
}
