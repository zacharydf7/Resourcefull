using Microsoft.Extensions.DependencyInjection;
using Resourcefull.InMemory;
using Resourcefull.Providers;
using Resourcefull.Testing;

namespace Resourcefull.Tests.Unit.InMemory;

/// <summary>
/// The contract run against a provider that implements only the abstract members, as a new provider starts out: every
/// optional feature is switched off, so its tests check that the feature is refused.
/// </summary>
public class MinimalProviderContractTests : ResourcefullContractTests
{
    protected override ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseProvider(services => new MinimalRepository(new ResourceInMemoryRepository(services.GetService<TimeProvider>())),
                ServiceLifetime.Singleton);
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    protected override bool SupportsContainerMetadata => false;
    protected override bool SupportsVersionChanges => false;
    protected override bool SupportsMove => false;
    protected override bool SupportsImport => false;
    protected override bool SupportsExpiry => false;

    /// <summary>Passes on only the members every provider must implement, leaving the rest to their defaults.</summary>
    private sealed class MinimalRepository(ResourceRepository inner) : ResourceRepository
    {
        public override Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
            ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) =>
            inner.AddResourceAsync(version, content, condition, cancellationToken);

        public override Task<IReadOnlyList<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
            CancellationToken cancellationToken = default) =>
            inner.GetAllResourceInfoAsync(resourceContainerName, cancellationToken);

        public override Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceLocation resource,
            CancellationToken cancellationToken = default) =>
            inner.GetResourceVersionsAsync(resource, cancellationToken);

        public override Task<ResourceInfo?> GetResourceInfoAsync(ResourceLocation resource, string? versionId = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResourceInfoAsync(resource, versionId, cancellationToken);

        public override Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResourceStreamAsync(resource, versionId, cancellationToken);

        public override Task<bool> DeleteResourceAsync(ResourceLocation resource, string? versionId = null,
            ResourceConditions? conditions = null, CancellationToken cancellationToken = default) =>
            inner.DeleteResourceAsync(resource, versionId, conditions, cancellationToken);

        public override Task AddContainerAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken = default) =>
            inner.AddContainerAsync(resourceContainerName, metadata, cancellationToken);

        public override Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName, int pageSize,
            CancellationToken cancellationToken = default) =>
            inner.GetContainerPageAsync(prefix, afterName, pageSize, cancellationToken);

        public override Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
            CancellationToken cancellationToken = default) =>
            inner.GetContainerInfoAsync(resourceContainerName, cancellationToken);

        public override Task<bool> DeleteContainerAsync(string resourceContainerName,
            CancellationToken cancellationToken = default) =>
            inner.DeleteContainerAsync(resourceContainerName, cancellationToken);
    }
}
