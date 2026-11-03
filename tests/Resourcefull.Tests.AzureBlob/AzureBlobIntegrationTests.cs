using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AzureBlob;
using Resourcefull.ObjectStorage;
using Resourcefull.Providers;

namespace Resourcefull.Tests.AzureBlob;

[Collection(AzureBlobCollection.Name)]
public class AzureBlobIntegrationTests(AzureBlobFixture fixture)
{
    /// <summary>
    /// A service provider on the test container, under <paramref name="keyPrefix"/> or a prefix of its own, whose client
    /// goes through <paramref name="policy"/>.
    /// </summary>
    private ServiceProvider BuildServiceProvider(InterferingPolicy? policy, Action<ResourcefullOptionsBuilder>? configure = null,
        string? keyPrefix = null)
    {
        // Two quick retries, so a request that keeps failing soon ends the way the SDK ends one: with an AggregateException.
        var options = new BlobClientOptions { Retry = { MaxRetries = 2, Delay = TimeSpan.FromMilliseconds(10) } };
        if (policy is not null) options.AddPolicy(policy, HttpPipelinePosition.PerRetry);
        var services = new ServiceCollection();
        services.AddResourcefull(builder =>
        {
            builder.UseAzureBlobStorage(fixture.ContainerName, azure =>
            {
                azure.Client = new BlobServiceClient(Environment.GetEnvironmentVariable("RESOURCEFULL_TEST_AZUREBLOB"), options);
                azure.KeyPrefix = keyPrefix ?? $"it-{Guid.NewGuid():N}/";
            });
            configure?.Invoke(builder);
        });
        return services.BuildServiceProvider();
    }

    private async Task<int> CountBlobsAsync(string prefix)
    {
        int count = 0;
        await foreach (BlobItem _ in fixture.Client.GetBlobContainerClient(fixture.ContainerName).GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }))
            count++;
        return count;
    }

    [Fact]
    public async Task UploadAsync_ShouldRetryAndCountTheRetry_WhenAnotherWriterChangesTheNameFirst()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name with one version, and a client that changes the name's head just before the next write of it.
        var policy = new InterferingPolicy(fixture.Client.GetBlobContainerClient(fixture.ContainerName));
        await using ServiceProvider provider = BuildServiceProvider(policy, o => o.UseVersioning());
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a.txt", BinaryData.FromString("first"));
        var retries = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.azureblob.retries") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => retries.Add(value));
        listener.Start();
        policy.Interfere = true;

        // Act
        await container.UploadAsync("a.txt", BinaryData.FromString("second"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert: the write lost the first race, tried again, and both versions are kept.
        Assert.Equal([1L], retries);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("a.txt")).Count);
        Assert.Equal("second", (await container.DownloadContentAsync("a.txt"))!.Content.ToString());
        Assert.False(policy.Interfere);
    }

    [Fact]
    public async Task ConditionalWrites_ShouldSucceedOnce_WhenAWriteLandsButItsAnswerIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a client that, when told to, sends the next conditional write and then loses the answer, so the SDK
        // sends it again and the store refuses the resend: 409 for a create, 412 for a change.
        var policy = new InterferingPolicy(fixture.Client.GetBlobContainerClient(fixture.ContainerName));
        await using ServiceProvider provider = BuildServiceProvider(policy, o => o.UseVersioning());
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();

        // Act
        policy.LoseAnswer = true;
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        policy.LoseAnswer = true;
        ResourceInfo first = await container.UploadAsync("a.txt", BinaryData.FromString("1"));
        policy.LoseAnswer = true;
        ResourceInfo second = await container.UploadAsync("a.txt", BinaryData.FromString("2"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert: each write was taken as done, once, and none was answered as a conflict.
        Assert.Equal([second.VersionId, first.VersionId], (await container.GetResourceVersionsAsync("a.txt")).Select(v => v.VersionId));
        Assert.Equal(3, policy.AnswersLost);
    }

    [Fact]
    public async Task DeleteResourceIfExistsAsync_ShouldReportTheDelete_WhenDeletingTheContentFails()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a client whose every blob delete fails after its retries, as when the account can't be reached.
        var policy = new InterferingPolicy(fixture.Client.GetBlobContainerClient(fixture.ContainerName));
        await using ServiceProvider provider = BuildServiceProvider(policy);
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a.txt", BinaryData.FromString("a"));
        policy.FailDeletes = true;

        // Act
        bool deleted = await container.DeleteResourceIfExistsAsync("a.txt");

        // Assert: the name is gone, which is the delete; its content stays behind, unnamed.
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("a.txt"));
        Assert.True(policy.DeletesFailed > 0);
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreContentInBlocks_WhenItIsLargerThanOnePart()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a part and a byte, so the upload takes two blocks.
        await using ServiceProvider provider = BuildServiceProvider(null);
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        byte[] payload = RandomNumberGenerator.GetBytes(ObjectParts.PartSize + 1);

        // Act
        ResourceInfo stored = await container.UploadAsync("big.bin", new MemoryStream(payload));
        byte[] whole = (await container.DownloadContentAsync("big.bin"))!.Content.ToArray();
        await using Stream? range = await provider.GetRequiredService<ResourceRepository>().GetResourceStreamAsync(
            new ResourceLocation("c", "big.bin"), null, ObjectParts.PartSize - 2, 4);
        using var ranged = new MemoryStream();
        await range!.CopyToAsync(ranged);

        // Assert: the whole content, and a range across the blocks' boundary.
        Assert.Equal(payload.LongLength, stored.ContentLength);
        Assert.Equal(payload, whole);
        Assert.Equal(payload.AsSpan(ObjectParts.PartSize - 2, 3).ToArray(), ranged.ToArray());
    }

    [Fact]
    public async Task UploadSession_ShouldJoinItsSegmentsInOrder_WhenItsAppendsSpanSeveral()
    {
        fixture.SkipIfUnavailable();

        // Arrange: segments of up to 9 MiB, and appends of 1 MiB and 20 MiB, which store three segments between them.
        string prefix = $"session-{Guid.NewGuid():N}/";
        var repository = new ObjectStoreRepository(new AzureBlobObjectStore(fixture.Client.GetBlobContainerClient(fixture.ContainerName)),
            prefix, null, null) { SegmentSize = 9 * 1024 * 1024 };
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        var location = new ResourceLocation("c", "big.bin");
        byte[] payload = RandomNumberGenerator.GetBytes(21 * 1024 * 1024);
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream(payload, 0, 1024 * 1024));
        await repository.AppendStagedContentAsync(location, "v1", 1024 * 1024, new MemoryStream(payload, 1024 * 1024, 20 * 1024 * 1024));
        int sessionBlobs = await CountBlobsAsync(prefix + "u/");

        // Act
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", payload.LongLength, "sha256:test");
        await using Stream? content = await repository.GetResourceStreamAsync(location);
        using var stored = new MemoryStream();
        await content!.CopyToAsync(stored);

        // Assert: the record and three segments; then the content, whole and in order, and nothing left of the session.
        Assert.Equal(4, sessionBlobs);
        Assert.Equal(payload.LongLength, written!.ContentLength);
        Assert.Equal(payload, stored.ToArray());
        Assert.Equal(0, await CountBlobsAsync(prefix + "u/"));
    }

    [Fact]
    public async Task AddResourceAsync_ShouldStoreTheContentInPiecesTheStoreJoins_WhenItIsLargerThanOneUploadTakes()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a store that takes 16 MiB in one upload, and 40 MiB of content, which takes three pieces.
        string prefix = $"pieces-{Guid.NewGuid():N}/";
        var store = new AzureBlobObjectStore(fixture.Client.GetBlobContainerClient(fixture.ContainerName))
        {
            MaxUploadLength = 2 * ObjectParts.PartSize
        };
        var repository = new ObjectStoreRepository(store, prefix, null, null);
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        byte[] payload = RandomNumberGenerator.GetBytes(40 * 1024 * 1024);
        var location = new ResourceLocation("c", "big.bin");

        // Act
        WrittenContent written = await repository.AddResourceAsync(new NewResourceVersion(location, "v1"), new MemoryStream(payload));
        await using Stream? content = await repository.GetResourceStreamAsync(location);
        using var stored = new MemoryStream();
        await content!.CopyToAsync(stored);

        // Assert: the content whole, and only the name's head and one content blob left.
        Assert.Equal(payload.LongLength, written.ContentLength);
        Assert.Equal(payload, stored.ToArray());
        Assert.Equal(2, await CountBlobsAsync(prefix + "d/"));
    }

    [Fact]
    public async Task MoveResourceAsync_ShouldCopyTheContentInParts_WhenMovedToAnotherContainer()
    {
        fixture.SkipIfUnavailable();

        // Arrange: copies in parts of 8 MiB, and a version of 17 MiB, which takes two, the last 1 MiB going with the second.
        var repository = new ObjectStoreRepository(new AzureBlobObjectStore(fixture.Client.GetBlobContainerClient(fixture.ContainerName)),
            $"move-parts-{Guid.NewGuid():N}/", null, null) { CopyPartSize = ObjectParts.PartSize };
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        await repository.AddContainerAsync("d", new Dictionary<string, string>());
        byte[] payload = RandomNumberGenerator.GetBytes(17 * 1024 * 1024);
        var from = new ResourceLocation("c", "big.bin");
        var to = new ResourceLocation("d", "big.bin");
        await repository.AddResourceAsync(new NewResourceVersion(from, "v1"), new MemoryStream(payload));

        // Act
        bool moved = await repository.MoveResourceAsync(from, to, null);
        await using Stream? content = await repository.GetResourceStreamAsync(to);
        using var stored = new MemoryStream();
        await content!.CopyToAsync(stored);

        // Assert
        Assert.True(moved);
        Assert.Equal(payload, stored.ToArray());
        Assert.Equal(0, (await repository.GetContainerUsageAsync("c")).VersionCount);
    }

    [Fact]
    public async Task UploadSession_ShouldComplete_WhenTheClientIsSignedWithASasInItsUrl()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a client for the blob container from a SAS URL, which can't sign links of its own.
        BlobContainerClient signed = fixture.Client.GetBlobContainerClient(fixture.ContainerName);
        var client = new BlobContainerClient(signed.GenerateSasUri(BlobContainerSasPermissions.All, DateTimeOffset.UtcNow.AddHours(1)));
        var repository = new ObjectStoreRepository(new AzureBlobObjectStore(client), $"sas-{Guid.NewGuid():N}/", null, null);
        await repository.AddContainerAsync("c", new Dictionary<string, string>());
        var location = new ResourceLocation("c", "a.txt");
        await repository.StartStagedVersionAsync(new NewResourceVersion(location, "v1"));
        await repository.AppendStagedContentAsync(location, "v1", 0, new MemoryStream("a"u8.ToArray()));

        // Act
        WrittenContent? written = await repository.CompleteStagedVersionAsync(location, "v1", 1, "sha256:test");
        await using Stream? content = await repository.GetResourceStreamAsync(location);
        using var stored = new MemoryStream();
        await content!.CopyToAsync(stored);

        // Assert
        Assert.False(client.CanGenerateSasUri);
        Assert.Equal(1, written!.ContentLength);
        Assert.Equal("a"u8.ToArray(), stored.ToArray());
    }

    [Fact]
    public async Task DeleteContainerAsync_ShouldDeleteEverythingTheContainerHeld_WhenCalled()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string keyPrefix = $"purge-{Guid.NewGuid():N}/";
        await using ServiceProvider provider = BuildServiceProvider(null, o => o.UseVersioning(), keyPrefix);
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        for (int i = 0; i < 3; i++) await container.UploadAsync($"{i}.txt", BinaryData.FromString("x"));
        await container.UploadAsync("0.txt", BinaryData.FromString("y"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });
        string prefix = keyPrefix + "d/";
        int stored = await CountBlobsAsync(prefix);

        // Act
        bool deleted = await resources.DeleteContainerIfExistsAsync("c");

        // Assert: three heads and four versions' content were there, and nothing is left.
        Assert.Equal(7, stored);
        Assert.True(deleted);
        Assert.Equal(0, await CountBlobsAsync(prefix));
    }

    [Fact]
    public async Task GetContainerInfoAsync_ShouldThrowRatherThanFindNothing_WhenTheBlobContainerDoesntExist()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a blob container nobody created, as a mistyped setting would give.
        var services = new ServiceCollection();
        services.AddResourcefull(o => o.UseAzureBlobStorage($"missing-{Guid.NewGuid():N}", azure => azure.Client = fixture.Client));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Exception? exception = await Record.ExceptionAsync(() => provider.GetRequiredService<IResourceServiceClient>().GetContainerInfoAsync("c"));

        // Assert
        Assert.Equal(BlobErrorCode.ContainerNotFound.ToString(), Assert.IsType<RequestFailedException>(exception).ErrorCode);
    }

    [Fact]
    public void UseAzureBlobStorage_ShouldThrow_WhenTheKeyPrefixIsTooLong()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        void act() => services.AddResourcefull(o => o.UseAzureBlobStorage("files", azure => azure.KeyPrefix = new string('a', 201)));

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public async Task UseAzureBlobStorage_ShouldSayToSetAClient_WhenNoneIsSetOrRegistered()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddResourcefull(o => o.UseAzureBlobStorage("files"));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Exception? exception = Record.Exception(() => provider.GetRequiredService<IResourceServiceClient>());

        // Assert
        Assert.Contains("BlobServiceClient", Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Passes every request on, except the next conditional write while a flag is set. With <see cref="Interfere"/>,
    /// the next conditional write to a head is preceded by an unconditional rewrite of that head, as another writer
    /// would make: the same content with a space appended, so its ETag changes and the conditional write fails. With
    /// <see cref="LoseAnswer"/>, the next conditional write is sent and its answer then lost, so the SDK sends it again.
    /// Each of those flags clears itself once used. With <see cref="FailDeletes"/>, every delete fails before it's sent.
    /// </summary>
    private sealed class InterferingPolicy(BlobContainerClient plain) : HttpPipelinePolicy
    {
        public bool Interfere { get; set; }

        public bool LoseAnswer { get; set; }

        public int AnswersLost { get; private set; }

        public bool FailDeletes { get; set; }

        public int DeletesFailed { get; private set; }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            Request request = message.Request;
            if (FailDeletes && request.Method == RequestMethod.Delete)
            {
                DeletesFailed++;
                throw new IOException("The account can't be reached.");
            }
            bool conditional = request.Method == RequestMethod.Put
                && (request.Headers.TryGetValue("If-Match", out _) || request.Headers.TryGetValue("If-None-Match", out _));
            if (conditional && Interfere && BlobName(request) is { } name && name.Contains("/h/", StringComparison.Ordinal))
            {
                Interfere = false;
                BlobClient head = plain.GetBlobClient(name);
                byte[] content = (await head.DownloadContentAsync()).Value.Content.ToArray();
                await head.UploadAsync(BinaryData.FromBytes([.. content, (byte)' ']), overwrite: true);
            }
            await ProcessNextAsync(message, pipeline);
            if (conditional && LoseAnswer)
            {
                LoseAnswer = false;
                AnswersLost++;
                throw new IOException("The answer was lost.");
            }
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
            ProcessAsync(message, pipeline).AsTask().GetAwaiter().GetResult();

        /// <summary>
        /// The blob a request is for: its path after the container's name. Azurite puts the account's name in the path
        /// first, so this searches for the container rather than taking the first segment.
        /// </summary>
        private string? BlobName(Request request)
        {
            string path = Uri.UnescapeDataString(request.Uri.Path);
            string container = "/" + plain.Name + "/";
            int at = path.IndexOf(container, StringComparison.Ordinal);
            return at < 0 ? null : path[(at + container.Length)..];
        }
    }
}
