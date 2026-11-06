using Microsoft.Extensions.DependencyInjection;
using Resourcefull.Providers;

namespace Resourcefull.Tests.FileSystem;

/// <summary>What the object stores' bin listing does with a prefix, on the provider that's quickest to fill a bin with.</summary>
[Collection(FileSystemCollection.Name)]
public class BinListingTests(FileSystemFixture fixture)
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task GetDeletedPageAsync_ShouldReturnEachMatchOnce_WhenAPrefixLeavesAPageShortAtTheLimitOfEntriesExamined()
    {
        // Arrange: a bin deleted all at once, so it lists by version ID. A page of 10 reads 10 entries and then 9 at a
        // time, so it reaches the limit of 1,000 at the first of the two "keep" names that share an ID, and goes on to
        // the second before it stops. More names that don't match follow, and then one last match.
        using ServiceProvider provider = fixture.BuildServiceProvider(o =>
        {
            o.UseExpiry().UseContainerPolicies(new ContainerPolicy { KeepDeletedFor = TimeSpan.FromDays(30) });
            o.Services.AddSingleton<TimeProvider>(new FixedClock());
        }, fixture.NewRoot());
        IResourceServiceClient service = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await service.CreateContainerAsync("c");
        var versions = new List<(string Name, string VersionId)> { ("keep-a", "v0000") };
        versions.AddRange(Enumerable.Range(1, 998).Select(i => ($"other-{i}", $"v{i:D4}")));
        versions.AddRange([("keep-b", "v0999"), ("keep-c", "v0999")]);
        versions.AddRange(Enumerable.Range(1000, 5).Select(i => ($"other-{i}", $"v{i:D4}")));
        versions.Add(("keep-z", "v1005"));

        async IAsyncEnumerable<ImportedVersion> Imports()
        {
            foreach ((string name, string versionId) in versions)
            {
                yield return new ImportedVersion(new ResourceInfo
                {
                    ContainerName = "", Name = name, VersionId = versionId, LastModified = new FixedClock().GetUtcNow()
                }, _ => Task.FromResult<Stream?>(new MemoryStream([1])));
            }
            await Task.CompletedTask;
        }
        await service.ImportVersionsAsync("c", Imports());
        await Parallel.ForEachAsync(versions, async (version, ct) =>
            await container.DeleteResourceIfExistsAsync(version.Name, cancellationToken: ct));

        // Act
        var options = new ResourceListOptions { Prefix = "keep-", PageSize = 10 };
        ResourcePage first = await container.GetDeletedPageAsync(options);
        ResourcePage second = await container.GetDeletedPageAsync(options, first.ContinuationToken);

        // Assert: the first stopped looking with room to spare, and the second carried on after what it looked at.
        Assert.Equal(["keep-a", "keep-b", "keep-c"], first.Resources.Select(v => v.Name));
        Assert.NotNull(first.ContinuationToken);
        Assert.Equal(["keep-z"], second.Resources.Select(v => v.Name));
        Assert.Null(second.ContinuationToken);
    }
}
