using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Resourcefull.Tests.Unit;

public class StoreCopyTests : IDisposable
{
    // Two stores, each with its own in-memory storage: the one copied from, and the one copied to.
    private readonly ServiceProvider _services = new ServiceCollection()
        .AddResourcefull(o => o.UseInMemory().UseVersioning())
        .AddKeyedResourcefull("target", o => o.UseInMemory().UseVersioning())
        .BuildServiceProvider();

    private IResourceServiceClient Source => _services.GetRequiredService<IResourceServiceClient>();

    private IResourceServiceClient Target => _services.GetRequiredKeyedService<IResourceServiceClient>("target");

    public void Dispose() => _services.Dispose();

    private static ResourceUploadOptions Replacing(string key, string value) =>
        new() { Mode = ResourceWriteMode.Replace, Metadata = new Dictionary<string, string> { [key] = value } };

    [Fact]
    public async Task CopyContainerToAsync_ShouldCopyEveryVersionAsItWas_WhenCopyingToAnotherStore()
    {
        // Arrange
        IResourceContainerClient invoices = await Source.CreateContainerAsync("invoices", new Dictionary<string, string> { ["team"] = "billing" });
        await invoices.UploadAsync("a.pdf", BinaryData.FromString("first"), new ResourceUploadOptions
        {
            Headers =
                new ResourceHeaders { ContentType = "application/pdf" }, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        await invoices.UploadAsync("a.pdf", BinaryData.FromString("second"), Replacing("k", "v"));
        await invoices.UploadAsync("b.pdf", BinaryData.FromString("b"), new ResourceUploadOptions
        {
            RetainUntil = DateTimeOffset.UtcNow.AddDays(1), LegalHold = true
        });

        // Act
        ContainerCopyResult copied = await Source.CopyContainerToAsync("invoices", Target);

        // Assert
        Assert.Equal(("invoices", "invoices", 2L, 3L, 12L), (copied.SourceContainerName, copied.DestinationContainerName,
            copied.ResourceCount, copied.VersionCount, copied.ContentSize.Bytes));
        Assert.Equal(new Dictionary<string, string> { ["team"] = "billing" }, (await Target.GetContainerClient("invoices").GetInfoAsync())!.Metadata);
        IResourceContainerClient copy = Target.GetContainerClient("invoices");
        foreach (string name in new[] { "a.pdf", "b.pdf" })
        {
            Assert.Equivalent((await invoices.GetResourceVersionsAsync(name)).Select(v => v with { Uri = null }),
                (await copy.GetResourceVersionsAsync(name)).Select(v => v with { Uri = null }), strict: true);
        }
        Assert.Equal("second", (await copy.DownloadContentAsync("a.pdf"))!.Content.ToString());
        await copy.SetLegalHoldAsync("b.pdf", null, false);
        await invoices.SetLegalHoldAsync("b.pdf", null, false);
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldRefuseAndChangeNothing_WhenTheDestinationHasTheContainer()
    {
        // Arrange
        await (await Source.CreateContainerAsync("c")).UploadAsync("a", BinaryData.FromString("a"));
        await (await Target.CreateContainerAsync("c")).UploadAsync("theirs", BinaryData.FromString("x"));

        // Act & Assert
        await Assert.ThrowsAsync<ResourceConflictException>(() => Source.CopyContainerToAsync("c", Target));
        Assert.Equal(["theirs"], (await Target.GetContainerClient("c").ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Theory]
    [InlineData(ExistingContainers.Fail)]
    [InlineData(ExistingContainers.SkipMatching)]
    [InlineData(ExistingContainers.Update)]
    public async Task CopyContainerToAsync_ShouldCopyTheContainer_WhenTheDestinationLacksItWhateverExistingSays(ExistingContainers existing)
    {
        // Arrange
        await (await Source.CreateContainerAsync("c")).UploadAsync("a", BinaryData.FromString("a"));

        // Act
        ContainerCopyResult copied = await Source.CopyContainerToAsync("c", Target, new ContainerCopyOptions { Existing = existing });

        // Assert
        Assert.Equal((ContainerCopyOutcome.Copied, "c", 1L), (copied.Outcome, copied.DestinationContainerName, copied.VersionCount));
        Assert.Equal("a", (await Target.GetContainerClient("c").DownloadContentAsync("a"))!.Content.ToString());
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldLeaveAContainerWithALongerName_WhenUpdatingOneContainer()
    {
        // Arrange: a name a store copy's prefix would also match.
        await (await Source.CreateContainerAsync("docs")).UploadAsync("a", BinaryData.FromString("a"));
        await (await Source.CreateContainerAsync("docs-archive")).UploadAsync("b", BinaryData.FromString("b"));
        await Target.CreateContainerAsync("docs");

        // Act
        ContainerCopyResult updated = await Source.CopyContainerToAsync("docs", Target, new ContainerCopyOptions { Existing = ExistingContainers.Update });

        // Assert
        Assert.Equal(ContainerCopyOutcome.Updated, updated.Outcome);
        Assert.False(await Target.ContainerExistsAsync("docs-archive"));
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldGiveTheDestinationTheSourcesMetadataAndCallThatUpdated_WhenOnlyTheMetadataDiffers()
    {
        // Arrange
        await Source.CreateContainerAsync("c", new Dictionary<string, string> { ["team"] = "billing" });
        await Target.CreateContainerAsync("c");

        // Act
        ContainerCopyResult updated = await Source.CopyContainerToAsync("c", Target, new ContainerCopyOptions { Existing = ExistingContainers.Update });

        // Assert
        Assert.Equal(ContainerCopyOutcome.Updated, updated.Outcome);
        Assert.Equal(new ContainerUpdateResult { Name = "c", Imported = 0, Changed = 0, Deleted = 0 }, updated.Update);
        Assert.Equal("billing", (await Target.GetContainerClient("c").GetInfoAsync())!.Metadata["team"]);
    }

    [Fact]
    public async Task CopyContainerToAsync_ShouldThrowOutOfRange_WhenExistingIsntDefined()
    {
        // Arrange
        await Source.CreateContainerAsync("c");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Source.CopyContainerToAsync("c", Target, new ContainerCopyOptions { Existing = (ExistingContainers)99 }));
        Assert.False(await Target.ContainerExistsAsync("c"));
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldCopyEachContainerInThePrefixAndListTheDestinationsOwn_WhenTheDestinationHasNone()
    {
        // Arrange
        await (await Source.CreateContainerAsync("app-one")).UploadAsync("a", BinaryData.FromString("a"));
        await Source.CreateContainerAsync("app-two");
        await Source.CreateContainerAsync("other");
        await Target.CreateContainerAsync("app-theirs");
        var reported = new List<ContainerCopied>();

        // Act
        StoreCopyResult result = await Source.CopyStoreToAsync(Target, new StoreCopyOptions
        {
            Prefix = "app-", Progress = new SynchronousProgress<ContainerCopied>(reported.Add)
        });

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(["app-one", "app-two"], result.Copied.Select(c => c.DestinationContainerName));
        Assert.Equal(["app-theirs"], result.DestinationOnly);
        Assert.Equal([new ContainerCopied
        {
            Name = "app-one",
            Outcome = ContainerCopyOutcome.Copied
        }, new ContainerCopied { Name = "app-two", Outcome = ContainerCopyOutcome.Copied }], reported);
        Assert.False(await Target.ContainerExistsAsync("other"));
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldSkipWhatMatchesAndFailWhatDoesnt_WhenSkippingMatching()
    {
        // Arrange: two containers copied, then one of them changed.
        await (await Source.CreateContainerAsync("same")).UploadAsync("a", BinaryData.FromString("a"));
        IResourceContainerClient changed = await Source.CreateContainerAsync("changed");
        await changed.UploadAsync("a", BinaryData.FromString("a"));
        await Source.CopyStoreToAsync(Target);
        await changed.SetResourceMetadataAsync("a", null, new Dictionary<string, string> { ["k"] = "v" });

        // Act
        StoreCopyResult result = await Source.CopyStoreToAsync(Target, new StoreCopyOptions
        {
            Existing = ExistingContainers.SkipMatching, OnFailure = StoreCopyFailures.Continue
        });

        // Assert
        Assert.Equal(["same"], result.Matching);
        ContainerCopyFailure failure = Assert.Single(result.Failed);
        Assert.Equal("changed", failure.Name);
        Assert.Contains("a version", Assert.IsType<ResourceConflictException>(failure.Exception).Message);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldBringTheDestinationUpToDateUntilItMatches_WhenUpdating()
    {
        // Arrange: a copy, then a new version of a, other metadata on b's, c deleted and d added.
        IResourceContainerClient files = await Source.CreateContainerAsync("files");
        foreach (string name in new[] { "a", "b", "c" }) await files.UploadAsync(name, BinaryData.FromString(name));
        await Source.CopyStoreToAsync(Target);
        await files.UploadAsync("a", BinaryData.FromString("a2"), Replacing("k", "new"));
        await files.SetResourceMetadataAsync("b", null, new Dictionary<string, string> { ["k"] = "v" });
        await files.DeleteResourceIfExistsAsync("c");
        await files.UploadAsync("d", BinaryData.FromString("d"));

        // Act
        StoreCopyResult updated = await Source.CopyStoreToAsync(Target, new StoreCopyOptions { Existing = ExistingContainers.Update });
        StoreCopyResult checkedAfter = await Source.CopyStoreToAsync(Target, new StoreCopyOptions { Existing = ExistingContainers.SkipMatching });

        // Assert
        Assert.Equal(new ContainerUpdateResult
        {
            Name = "files",
            Imported = 2,
            Changed = 1,
            Deleted = 1
        }, Assert.Single(updated.Updated));
        Assert.Equal(["files"], checkedAfter.Matching);
        Assert.Equal("a2", (await Target.GetContainerClient("files").DownloadContentAsync("a"))!.Content.ToString());
        Assert.False(await Target.GetContainerClient("files").ExistsAsync("c"));
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldRefuseAndChangeNothing_WhenUpdatingNeedsAnOlderVersionPutBack()
    {
        // Arrange: the destination lost the older of a's two versions.
        IResourceContainerClient files = await Source.CreateContainerAsync("files");
        ResourceInfo older = await files.UploadAsync("a", BinaryData.FromString("1"));
        await files.UploadAsync("a", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        await Source.CopyStoreToAsync(Target);
        await Target.GetContainerClient("files").DeleteResourceIfExistsAsync("a", older.VersionId);

        // Act
        StoreCopyException stopped = await Assert.ThrowsAsync<StoreCopyException>(() =>
            Source.CopyStoreToAsync(Target, new StoreCopyOptions { Existing = ExistingContainers.Update }));

        // Assert
        Assert.Contains("Copy it afresh", stopped.InnerException!.Message);
        Assert.Single(await Target.GetContainerClient("files").GetResourceVersionsAsync("a"));
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldStopWithWhatItDid_WhenAContainerFailsAndItStopsOnFailure()
    {
        // Arrange: the second of three containers is in the destination already.
        foreach (string name in new[] { "c1", "c2", "c3" }) await Source.CreateContainerAsync(name);
        await Target.CreateContainerAsync("c2");

        // Act
        StoreCopyException stopped = await Assert.ThrowsAsync<StoreCopyException>(() => Source.CopyStoreToAsync(Target));

        // Assert
        Assert.Equal(["c1"], stopped.Result.Copied.Select(c => c.SourceContainerName));
        Assert.Equal("c2", Assert.Single(stopped.Result.Failed).Name);
        Assert.IsType<ResourceConflictException>(stopped.InnerException);
        Assert.False(await Target.ContainerExistsAsync("c3"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyStoreToAsync_ShouldLeaveARunningImportAndFinishAStoppedOne_WhenUpdating(bool stopped)
    {
        // Arrange: the destination's container has the first of two versions, and the mark of an import, written just
        // now or an hour ago.
        IResourceContainerClient files = await Source.CreateContainerAsync("files", new Dictionary<string, string> { ["team"] = "a" });
        await files.UploadAsync("a", BinaryData.FromString("a"));
        await Source.CopyStoreToAsync(Target);
        await files.UploadAsync("b", BinaryData.FromString("b"));
        DateTimeOffset markedAt = DateTimeOffset.UtcNow - (stopped ? TimeSpan.FromHours(1) : TimeSpan.Zero);
        await Target.GetContainerClient("files").SetMetadataAsync(new Dictionary<string, string>
        {
            ["team"] = "a", ["resourcefull.import"] = "in-progress", ["resourcefull.import_at"] = markedAt.ToString("O")
        });

        // Act
        StoreCopyResult result = await Source.CopyStoreToAsync(Target, new StoreCopyOptions
        {
            Existing = ExistingContainers.Update, OnFailure = StoreCopyFailures.Continue
        });

        // Assert
        if (stopped)
        {
            Assert.Equal(new ContainerUpdateResult
            {
                Name = "files",
                Imported = 1,
                Changed = 0,
                Deleted = 0
            }, Assert.Single(result.Updated));
            Assert.Equal(new Dictionary<string, string> { ["team"] = "a" }, (await Target.GetContainerClient("files").GetInfoAsync())!.Metadata);
        }
        else
        {
            Assert.Contains("still being copied", Assert.Single(result.Failed).Exception.Message);
            Assert.False(await Target.GetContainerClient("files").ExistsAsync("b"));
        }
    }

    /// <summary>A version to import, named <paramref name="name"/>, with <paramref name="content"/> and a hash that may not be its own.</summary>
    private static ImportedVersion Version(string name, string versionId, string content, string? hash = null) =>
        new() { Version = new ResourceInfo
        {
            ContainerName = "", Name = name, VersionId = versionId, LastModified = DateTimeOffset.UnixEpoch, ContentHash = hash
        }, OpenContentAsync = _ => Task.FromResult<Stream?>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))) };

    private const string WrongHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public async Task ImportVersionsAsync_ShouldThrowAndLeaveNoVersion_WhenAContentDoesNotMatchItsHash()
    {
        // Arrange
        await Target.CreateContainerAsync("c");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Target.ImportVersionsAsync("c", new[] { Version("a", "v1", "changed", WrongHash) }.ToAsyncEnumerable()));
        Assert.False(await Target.GetContainerClient("c").ExistsAsync("a"));
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldDeleteTheContainer_WhenAContentDoesNotMatchItsHash()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => Target.ImportContainerAsync("c",
            new[] { Version("a", "v1", "fine"), Version("b", "v1", "changed", WrongHash) }.ToAsyncEnumerable()));
        Assert.False(await Target.ContainerExistsAsync("c"));
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldSkipAVersion_WhenItsContentIsGone()
    {
        // Arrange: b was deleted from its store after it was listed.
        ImportedVersion gone = Version("b", "v1", "") with { OpenContentAsync = _ => Task.FromResult<Stream?>(null) };

        // Act
        ContainerImportResult imported = await Target.ImportContainerAsync("c", new[] { Version("a", "v1", "a"), gone }.ToAsyncEnumerable());

        // Assert
        Assert.Equal((1L, 1L), (imported.ResourceCount, imported.VersionCount));
        Assert.False(await Target.GetContainerClient("c").ExistsAsync("b"));
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldRefuseToUpdate_WhenTheDestinationRetainsAVersionLonger()
    {
        // Arrange
        IResourceContainerClient files = await Source.CreateContainerAsync("files");
        await files.UploadAsync("a", BinaryData.FromString("a"));
        await Source.CopyStoreToAsync(Target);
        await Target.GetContainerClient("files").SetRetentionAsync("a", null, DateTimeOffset.UtcNow.AddMinutes(1));

        // Act
        StoreCopyException stopped = await Assert.ThrowsAsync<StoreCopyException>(() =>
            Source.CopyStoreToAsync(Target, new StoreCopyOptions { Existing = ExistingContainers.Update }));

        // Assert
        Assert.Contains("retained longer", stopped.InnerException!.Message);
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldRefuseToUpdate_WhenTheDestinationHasTheVersionsInAnotherOrder()
    {
        // Arrange: the destination imported a's two versions newest first.
        IResourceContainerClient files = await Source.CreateContainerAsync("files");
        await files.UploadAsync("a", BinaryData.FromString("1"));
        await files.UploadAsync("a", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        IReadOnlyList<ResourceInfo> newestFirst = await files.GetResourceVersionsAsync("a");
        await Target.ImportContainerAsync("files", newestFirst.Select(v => new ImportedVersion
        {
            Version = v,
            OpenContentAsync = _ => Task.FromResult<Stream?>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(v.IsLatest ? "2" : "1")))
        }).ToAsyncEnumerable());

        // Act
        StoreCopyException stopped = await Assert.ThrowsAsync<StoreCopyException>(() =>
            Source.CopyStoreToAsync(Target, new StoreCopyOptions { Existing = ExistingContainers.Update }));

        // Assert
        Assert.Contains("in another order", stopped.InnerException!.Message);
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldFailASourceContainer_WhenItsOwnImportHasntFinished()
    {
        // Arrange
        await Source.CreateContainerAsync("half", new Dictionary<string, string> { ["resourcefull.import"] = "in-progress" });

        // Act
        StoreCopyResult result = await Source.CopyStoreToAsync(Target, new StoreCopyOptions { OnFailure = StoreCopyFailures.Continue });

        // Assert
        Assert.Contains("hasn't finished", Assert.Single(result.Failed).Exception.Message);
        Assert.False(await Target.ContainerExistsAsync("half"));
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldThrowRatherThanReportAFailure_WhenCanceledPartway()
    {
        // Arrange: the copy is canceled once the first of two containers is done.
        await Source.CreateContainerAsync("c1");
        await Source.CreateContainerAsync("c2");
        using var canceling = new CancellationTokenSource();

        // Act & Assert: stopping at a failure, as by default, a cancellation taken for one would throw StoreCopyException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Source.CopyStoreToAsync(Target, new StoreCopyOptions
        {
            Progress = new SynchronousProgress<ContainerCopied>(_ => canceling.Cancel())
        }, canceling.Token));
        Assert.False(await Target.ContainerExistsAsync("c2"));
    }

    [Fact]
    public async Task ImportContainerAsync_ShouldCountEachNameOnce_WhenItsVersionsAreInterleaved()
    {
        // Act
        ContainerImportResult imported = await Target.ImportContainerAsync("c",
            new[] { Version("a", "v1", "a1"), Version("b", "v1", "b1"), Version("a", "v2", "a2") }.ToAsyncEnumerable());

        // Assert
        Assert.Equal((2L, 3L), (imported.ResourceCount, imported.VersionCount));
    }

    [Fact]
    public async Task ImportVersionsAsync_ShouldKeepAStoredVersionRetained_WhenALaterVersionFails()
    {
        // Arrange: a retained version, then one whose content doesn't match its hash.
        await Target.CreateContainerAsync("c");
        DateTimeOffset retainUntil = DateTimeOffset.UtcNow.AddHours(1);
        ImportedVersion retained = Version("a", "v1", "a");
        retained = retained with { Version = retained.Version with { RetainUntil = retainUntil } };

        // Act
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Target.ImportVersionsAsync("c", new[] { retained, Version("b", "v1", "changed", WrongHash) }.ToAsyncEnumerable()));

        // Assert: what was stored stays, as retained as it was.
        Assert.Equal(retainUntil, (await Target.GetContainerClient("c").GetResourceInfoAsync("a"))!.RetainUntil);
    }

    [Fact]
    public async Task CopyStoreToAsync_ShouldFindAVersionMatching_WhenItsRecordedLengthIsntItsContentsButItsHashMatches()
    {
        // Arrange: a source whose version records a length its content doesn't have, as one stored before lengths were
        // measured may, and a destination holding the same content, with the length measured.
        string hash = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("a"u8));
        var version = new ResourceInfo
        {
            ContainerName = "c", Name = "a", VersionId = "v1", IsLatest = true, ContentHash = hash, ContentLength = 999,
            LastModified = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var container = new ContainerInfo { Name = "c" };
        var repository = new Moq.Mock<Providers.ResourceRepository>();
        repository.Setup(r => r.GetContainerPageAsync(null, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([container]);
        repository.Setup(r => r.GetContainerInfoAsync("c", It.IsAny<CancellationToken>())).ReturnsAsync(container);
        repository.Setup(r => r.GetVersionPageAsync("c", null, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([version]);
        repository.Setup(r => r.GetVersionPageAsync("c", "a", It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        repository.Setup(r => r.GetResourceInfoAsync(new Providers.ResourceAddress("c", "a"), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(version);
        var source = new ResourceServiceClient(repository.Object, Microsoft.Extensions.Options.Options.Create(new ResourcefullOptions()));
        await Target.ImportContainerAsync("c", new[] { new ImportedVersion
        {
            Version = version with { ContentLength = null },
            OpenContentAsync = _ => Task.FromResult<Stream?>(new MemoryStream("a"u8.ToArray()))
        } }.ToAsyncEnumerable());

        // Act
        StoreCopyResult result = await source.CopyStoreToAsync(Target, new StoreCopyOptions { Existing = ExistingContainers.SkipMatching });

        // Assert
        Assert.Equal(["c"], result.Matching);
    }

    /// <summary>Reports on the thread that calls it, unlike <see cref="Progress{T}"/>, so a test can read what it got at once.</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
