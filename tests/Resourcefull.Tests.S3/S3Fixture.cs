using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;

namespace Resourcefull.Tests.S3;

/// <summary>
/// Provisions a throwaway bucket for the suite, on the S3-compatible store whose endpoint <c>RESOURCEFULL_TEST_S3</c>
/// names, such as <c>http://localhost:18333</c>. Without it, tests skip; a store it names that can't be reached fails the
/// suite. Each run gets its own bucket, emptied and deleted on teardown.
/// </summary>
public sealed class S3Fixture : IAsyncLifetime
{
    private const string EndpointEnvVar = "RESOURCEFULL_TEST_S3";

    /// <summary>Whether a store was named and the test bucket was created.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The client the tests reach the store with.</summary>
    public IAmazonS3 Client { get; private set; } = null!;

    /// <summary>The run's bucket.</summary>
    public string BucketName { get; } = $"resourcefull-it-{Guid.NewGuid():N}"[..40];

    /// <summary>Skips the calling test, rather than failing it, when no store is named.</summary>
    public void SkipIfUnavailable() =>
        Assert.SkipUnless(IsAvailable, $"No S3-compatible store named. Set {EndpointEnvVar} to run the S3 tests.");

    public async ValueTask InitializeAsync()
    {
        string? endpoint = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        Client = new AmazonS3Client(new BasicAWSCredentials("resourcefull", "resourcefull"), new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            // Self-hosted stores may not take the trailing checksums the SDK otherwise sends.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
        await WaitForStoreAsync();
        await Client.PutBucketAsync(BucketName);
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable) return;
        while (true)
        {
            ListObjectsV2Response listed = await Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = BucketName });
            if (listed.S3Objects is not { Count: > 0 } objects) break;
            await Client.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = BucketName, Objects = [.. objects.Select(o => new KeyVersion { Key = o.Key })]
            });
        }
        await Client.DeleteBucketAsync(BucketName);
        Client.Dispose();
    }

    /// <summary>
    /// Builds a fully wired Resourcefull service provider on the test bucket, under a key prefix of its own.
    /// </summary>
    public ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null, string keyPrefix = "resourcefull/")
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseS3(BucketName, s3 =>
            {
                s3.Client = Client;
                s3.KeyPrefix = keyPrefix;
            });
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    private async Task WaitForStoreAsync()
    {
        // A store just started in a Docker container needs time to accept requests.
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await Client.ListBucketsAsync();
                return;
            }
            catch (Exception exception) when (exception is AmazonServiceException or HttpRequestException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class S3Collection : ICollectionFixture<S3Fixture>
{
    public const string Name = "S3";
}
