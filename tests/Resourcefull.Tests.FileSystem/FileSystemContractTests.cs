using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;
using Resourcefull.Testing;

namespace Resourcefull.Tests.FileSystem;

[Collection(FileSystemCollection.Name)]
public class FileSystemContractTests(FileSystemFixture fixture) : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null) =>
        fixture.BuildServiceProvider(configure);

    protected override IComparer<string> NameOrder => CodePointOrder.Instance;

    [Fact]
    public void OptionalFeatureFlags_ShouldBeTrue_WhenGivenTheFileSystemProvider()
    {
        // A flag left false would turn the contract tests of that feature into skips, without failing anything.
        using ServiceProvider provider = fixture.BuildServiceProvider(null);
        using IServiceScope scope = provider.CreateScope();

        ResourceRepository repository = scope.ServiceProvider.GetRequiredService<ResourceRepository>();

        Assert.True(repository.SupportsExpiry);
        Assert.True(repository.SupportsRetention);
        Assert.True(repository.SupportsSoftDelete);
    }
}

/// <summary>The order keys list in: their UTF-8 bytes, which is Unicode code point order.</summary>
internal static class CodePointOrder
{
    public static IComparer<string> Instance { get; } = Comparer<string>.Create((x, y) =>
        Encoding.UTF8.GetBytes(x!).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y!)));
}
