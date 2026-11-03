using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Testing;

namespace Resourcefull.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class PostgreSqlContractTests(PostgreSqlFixture fixture) : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        fixture.SkipIfUnavailable();
        return fixture.BuildServiceProvider(configure);
    }

    protected override TimeSpan TimePrecision => TimeSpan.FromMicroseconds(1);

    protected override IComparer<string> NameOrder => CodePointOrder.Instance;
}

/// <summary>The contract again, with the tables in a schema of their own.</summary>
[Collection(PostgreSqlSchemaCollection.Name)]
public class PostgreSqlSchemaContractTests(PostgreSqlSchemaFixture fixture) : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        fixture.SkipIfUnavailable();
        return fixture.BuildServiceProvider(configure);
    }

    protected override TimeSpan TimePrecision => TimeSpan.FromMicroseconds(1);

    protected override IComparer<string> NameOrder => CodePointOrder.Instance;
}

/// <summary>The order PostgreSQL's "C" collation gives names: their UTF-8 bytes, which is Unicode code point order.</summary>
internal static class CodePointOrder
{
    public static IComparer<string> Instance { get; } = Comparer<string>.Create((x, y) =>
        Encoding.UTF8.GetBytes(x!).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y!)));
}
