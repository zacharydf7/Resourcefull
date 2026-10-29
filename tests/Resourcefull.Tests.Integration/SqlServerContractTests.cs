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
