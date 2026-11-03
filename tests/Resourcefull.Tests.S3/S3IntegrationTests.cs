using System.Diagnostics.Metrics;
using System.Reflection;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.S3;

namespace Resourcefull.Tests.S3;

[Collection(S3Collection.Name)]
public class S3IntegrationTests(S3Fixture fixture)
{
    private async Task<int> CountKeysAsync(string prefix)
    {
        ListObjectsV2Response listed = await fixture.Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = fixture.BucketName, Prefix = prefix
        });
        return listed.S3Objects?.Count ?? 0;
    }

    [Fact]
    public async Task UploadAsync_ShouldRetryAndCountTheRetry_WhenAnotherWriterChangesTheNameFirst()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name with one version, and a client that changes the name's head just before the next write of it.
        var interfering = DispatchProxy.Create<IAmazonS3, InterferingS3>();
        ((InterferingS3)(object)interfering).Target = fixture.Client;
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3(fixture.BucketName, s3 =>
        {
            s3.Client = interfering;
            s3.KeyPrefix = $"retry-{Guid.NewGuid():N}/";
        }).UseVersioning());
        await using ServiceProvider provider = services.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("c");
        await container.UploadAsync("a.txt", BinaryData.FromString("first"));
        var retries = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "resourcefull.s3.retries") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => retries.Add(value));
        listener.Start();
        ((InterferingS3)(object)interfering).Interfere = true;

        // Act
        await container.UploadAsync("a.txt", BinaryData.FromString("second"), new ResourceUploadOptions { Mode = ResourceWriteMode.Replace });

        // Assert: the write lost the first race, tried again, and both versions are kept.
        Assert.Equal([1L], retries);
        Assert.Equal(2, (await container.GetResourceVersionsAsync("a.txt")).Count);
        Assert.Equal("second", (await container.DownloadContentAsync("a.txt"))!.Content.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_ShouldKeepBothNames_WhenOneNameIsAnotherWhoseKeyEndsAtASegment(bool longerFirst)
    {
        fixture.SkipIfUnavailable();

        // Arrange: a name whose escaped form fills a segment, and the same name with more after it, which a store that
        // keeps keys as files would put under the first one as a directory.
        await using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateContainerAsync($"c-{Guid.NewGuid():N}");
        string shorter = new('a', S3Keys.SegmentBytes - S3Keys.LongestUnit + 1), longer = shorter + "b";
        string[] order = longerFirst ? [longer, shorter] : [shorter, longer];

        // Act
        foreach (string name in order) await container.UploadAsync(name, BinaryData.FromString(name.Length.ToString()));

        // Assert
        Assert.Equal([shorter.Length.ToString(), longer.Length.ToString()], [(await container.DownloadContentAsync(shorter))!.Content.ToString(),
            (await container.DownloadContentAsync(longer))!.Content.ToString()]);
        Assert.Equal([shorter, longer], (await container.ListResourcesAsync().ToListAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task UploadAsync_ShouldStoreContentInParts_WhenItIsLargerThanOnePart()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a part and a byte, so the upload takes two parts.
        await using ServiceProvider provider = fixture.BuildServiceProvider();
        IResourceContainerClient container = await provider.GetRequiredService<IResourceServiceClient>()
            .CreateContainerAsync($"c-{Guid.NewGuid():N}");
        byte[] payload = new byte[ResourceS3Repository.PartSize + 1];
        new Random(1042).NextBytes(payload);

        // Act
        ResourceInfo stored = await container.UploadAsync("large.bin", new MemoryStream(payload));

        // Assert
        Assert.Equal(payload.LongLength, stored.ContentLength);
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)), stored.ContentHash);
        Assert.Equal(payload, (await container.DownloadContentAsync("large.bin"))!.Content.ToArray());
    }

    [Fact]
    public async Task ConditionalWrites_ShouldSucceedOnce_WhenAWriteLandsButItsAnswerIsLost()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a client that, when told to, sends the next conditional write and then answers 412, as a store does
        // to the SDK's resend of a write whose answer was lost.
        var interfering = DispatchProxy.Create<IAmazonS3, InterferingS3>();
        var proxy = (InterferingS3)(object)interfering;
        proxy.Target = fixture.Client;
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3(fixture.BucketName, s3 =>
        {
            s3.Client = interfering;
            s3.KeyPrefix = $"lost-{Guid.NewGuid():N}/";
        }));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();

        // Act
        proxy.LoseAnswer = true;
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        proxy.LoseAnswer = true;
        ResourceInfo stored = await container.UploadAsync("a.txt", BinaryData.FromString("x"));
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("a.txt");
        proxy.LoseAnswer = true;
        bool deleted = await container.DeleteResourceIfExistsAsync("a.txt");

        // Assert: each write was taken as done, once, and none was answered as a conflict.
        Assert.Equal(stored.VersionId, Assert.Single(versions).VersionId);
        Assert.True(deleted);
        Assert.False(await container.ExistsAsync("a.txt"));
        Assert.False(proxy.LoseAnswer);
    }

    [Fact]
    public async Task ConditionalWrites_ShouldSucceedOnce_WhenAWriteLandsButAnotherWriterChangesItBeforeItsLostAnswerIsChecked()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a client that, when told to, sends the next conditional write, lets another writer rewrite the
        // object, and then answers 412, so the write can't be recognized by its ID.
        var interfering = DispatchProxy.Create<IAmazonS3, InterferingS3>();
        var proxy = (InterferingS3)(object)interfering;
        proxy.Target = fixture.Client;
        string prefix = $"lost-changed-{Guid.NewGuid():N}/";
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3(fixture.BucketName, s3 =>
        {
            s3.Client = interfering;
            s3.KeyPrefix = prefix;
        }));
        await using ServiceProvider provider = services.BuildServiceProvider();
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();

        // Act: the creation and the upload are followed by another write of their object, the deletion by the
        // container being created again, under a new ID.
        proxy.AnotherWrite = json => json["w"] = Guid.NewGuid().ToString("N");
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        proxy.AnotherWrite = json => json["w"] = Guid.NewGuid().ToString("N");
        ResourceInfo stored = await container.UploadAsync("a.txt", BinaryData.FromString("x"));
        IReadOnlyList<ResourceInfo> versions = await container.GetResourceVersionsAsync("a.txt");
        proxy.AnotherWrite = json =>
        {
            json["id"] = Guid.NewGuid().ToString("N");
            json["deleted"] = false;
            json["w"] = Guid.NewGuid().ToString("N");
        };
        bool deleted = await resources.DeleteContainerIfExistsAsync("c");

        // Assert: each write was taken as done, once: neither the creation nor the upload was answered as a conflict,
        // the deletion didn't report finding nothing to delete, and the container created again was left alone, while
        // what the deleted one held is gone.
        Assert.Equal(stored.VersionId, Assert.Single(versions).VersionId);
        Assert.True(deleted);
        Assert.True(await resources.ContainerExistsAsync("c"));
        Assert.Equal(0, await CountKeysAsync(prefix + "d/"));
        Assert.Null(proxy.AnotherWrite);
    }

    [Fact]
    public async Task DeleteContainerAsync_ShouldDeleteEverythingTheContainerHeld_WhenCalled()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        string prefix = $"delete-{Guid.NewGuid():N}/";
        await using ServiceProvider provider = fixture.BuildServiceProvider(keyPrefix: prefix);
        IResourceServiceClient resources = provider.GetRequiredService<IResourceServiceClient>();
        IResourceContainerClient container = await resources.CreateContainerAsync("c");
        for (int i = 0; i < 3; i++) await container.UploadAsync($"{i}.txt", BinaryData.FromString("x"));
        int before = await CountKeysAsync(prefix + "d/");

        // Act
        bool deleted = await resources.DeleteContainerIfExistsAsync("c");

        // Assert: its record stays behind, marked deleted, and nothing it held does.
        Assert.True(deleted);
        Assert.Equal(6, before);
        Assert.Equal(0, await CountKeysAsync(prefix + "d/"));
        Assert.False(await resources.ContainerExistsAsync("c"));
    }

    [Fact]
    public async Task UseS3_ShouldKeepEachStoreToItsOwnKeys_WhenTwoShareABucketUnderDifferentPrefixes()
    {
        fixture.SkipIfUnavailable();

        // Arrange
        await using ServiceProvider first = fixture.BuildServiceProvider(keyPrefix: $"first-{Guid.NewGuid():N}/");
        await using ServiceProvider second = fixture.BuildServiceProvider(keyPrefix: $"second-{Guid.NewGuid():N}/");

        // Act
        await first.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("shared-name");
        await second.GetRequiredService<IResourceServiceClient>().CreateContainerAsync("shared-name");

        // Assert: neither saw the other's container, so neither creation conflicted.
        Assert.True(await first.GetRequiredService<IResourceServiceClient>().ContainerExistsAsync("shared-name"));
        Assert.True(await second.GetRequiredService<IResourceServiceClient>().ContainerExistsAsync("shared-name"));
    }

    [Fact]
    public async Task GetContainerInfoAsync_ShouldThrowRatherThanFindNothing_WhenTheBucketDoesntExist()
    {
        fixture.SkipIfUnavailable();

        // Arrange: a bucket name nobody created, as a mistyped setting would give.
        var services = new ServiceCollection();
        services.AddResourcefull(options => options.UseS3($"missing-{Guid.NewGuid():N}", s3 => s3.Client = fixture.Client));
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Act
        Task act() => provider.GetRequiredService<IResourceServiceClient>().GetContainerInfoAsync("c");

        // Assert
        Assert.Equal("NoSuchBucket", (await Assert.ThrowsAsync<AmazonS3Exception>(act)).ErrorCode);
    }

    [Fact]
    public void UseS3_ShouldThrow_WhenTheKeyPrefixIsTooLong()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        void act() => services.AddResourcefull(options => options.UseS3("bucket", s3 => s3.KeyPrefix = new string('p', 201)));

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    /// <summary>
    /// Passes every call to <see cref="Target"/>, except the next conditional write while a flag or the action is set.
    /// With <see cref="LoseAnswer"/>, that write is sent and then answered with 412, as a store answers the SDK's
    /// resend of a write whose answer was lost. With <see cref="AnotherWrite"/>, another writer also rewrites the
    /// object in between, changing its JSON as that action does. With <see cref="Interfere"/>, the next conditional
    /// write to a head is preceded by an unconditional rewrite of that head, as another writer would make: the same
    /// content with a space appended, so its ETag changes and the conditional write fails. Each flag and the action
    /// clear themselves once used.
    /// </summary>
    public class InterferingS3 : DispatchProxy
    {
        public IAmazonS3 Target { get; set; } = null!;

        public bool Interfere { get; set; }

        public bool LoseAnswer { get; set; }

        public Action<JsonNode>? AnotherWrite { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAmazonS3.PutObjectAsync) && args![0] is PutObjectRequest request
                && (request.IfMatch is not null || request.IfNoneMatch is not null))
            {
                if (LoseAnswer || AnotherWrite is not null)
                {
                    Action<JsonNode>? anotherWrite = AnotherWrite;
                    LoseAnswer = false;
                    AnotherWrite = null;
                    return PutThenLoseTheAnswerAsync(request, anotherWrite, (CancellationToken)args[1]!);
                }
                if (Interfere && request.IfMatch is not null && request.Key.Contains("/h/", StringComparison.Ordinal))
                {
                    Interfere = false;
                    return InterfereThenPutAsync(request, (CancellationToken)args[1]!);
                }
            }
            return targetMethod.Invoke(Target, args);
        }

        private async Task<PutObjectResponse> PutThenLoseTheAnswerAsync(PutObjectRequest request, Action<JsonNode>? anotherWrite,
            CancellationToken cancellationToken)
        {
            await Target.PutObjectAsync(request, cancellationToken);
            if (anotherWrite is not null)
            {
                using GetObjectResponse written = await Target.GetObjectAsync(request.BucketName, request.Key, cancellationToken);
                JsonNode json = (await JsonNode.ParseAsync(written.ResponseStream, cancellationToken: cancellationToken))!;
                anotherWrite(json);
                await Target.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = request.BucketName, Key = request.Key, ContentBody = json.ToJsonString()
                }, cancellationToken);
            }
            throw new AmazonS3Exception("At least one of the pre-conditions you specified did not hold")
            {
                StatusCode = System.Net.HttpStatusCode.PreconditionFailed, ErrorCode = "PreconditionFailed"
            };
        }

        private async Task<PutObjectResponse> InterfereThenPutAsync(PutObjectRequest request, CancellationToken cancellationToken)
        {
            using GetObjectResponse current = await Target.GetObjectAsync(request.BucketName, request.Key, cancellationToken);
            using var reader = new StreamReader(current.ResponseStream);
            await Target.PutObjectAsync(new PutObjectRequest
            {
                BucketName = request.BucketName, Key = request.Key, ContentBody = await reader.ReadToEndAsync(cancellationToken) + " "
            }, cancellationToken);
            return await Target.PutObjectAsync(request, cancellationToken);
        }
    }
}
