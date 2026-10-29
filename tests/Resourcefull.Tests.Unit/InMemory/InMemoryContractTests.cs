using Microsoft.Extensions.DependencyInjection;
using Resourcefull.InMemory;
using Resourcefull.Tests.Contract;

namespace Resourcefull.Tests.Unit.InMemory;

public class InMemoryContractTests : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseInMemory();
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }
}
