using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Testing;

namespace Resourcefull.Tests.AzureBlob;

[Collection(AzureBlobCollection.Name)]
public class AzureBlobContractTests(AzureBlobFixture fixture) : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        fixture.SkipIfUnavailable();
        return fixture.BuildServiceProvider(configure);
    }
}
