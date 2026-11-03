using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Tests.Contract;

namespace Resourcefull.Tests.Integration;

[Collection(SqlServerCollection.Name)]
public class SqlServerContractTests(SqlServerFixture fixture) : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        fixture.SkipIfUnavailable();
        return fixture.BuildServiceProvider(configure);
    }
}

/// <summary>The contract again, with the tables in a schema of their own.</summary>
[Collection(SqlServerSchemaCollection.Name)]
public class SqlServerSchemaContractTests(SqlServerSchemaFixture fixture) : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        fixture.SkipIfUnavailable();
        return fixture.BuildServiceProvider(configure);
    }
}
