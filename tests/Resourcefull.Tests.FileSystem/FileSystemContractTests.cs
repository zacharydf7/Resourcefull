using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Testing;

namespace Resourcefull.Tests.FileSystem;

[Collection(FileSystemCollection.Name)]
public class FileSystemContractTests(FileSystemFixture fixture) : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null) =>
        fixture.BuildServiceProvider(configure);

    protected override IComparer<string> NameOrder => CodePointOrder.Instance;
}

/// <summary>The order keys list in: their UTF-8 bytes, which is Unicode code point order.</summary>
internal static class CodePointOrder
{
    public static IComparer<string> Instance { get; } = Comparer<string>.Create((x, y) =>
        Encoding.UTF8.GetBytes(x!).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y!)));
}
