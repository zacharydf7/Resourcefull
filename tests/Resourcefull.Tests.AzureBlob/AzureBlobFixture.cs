using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.AzureBlob;

namespace Resourcefull.Tests.AzureBlob;

/// <summary>
/// Provisions a throwaway blob container for the suite, in the storage account whose connection string
/// <c>RESOURCEFULL_TEST_AZUREBLOB</c> holds, such as Azurite's <c>UseDevelopmentStorage=true</c>. Without it, tests skip;
/// an account it names that can't be reached fails the suite. Each run gets its own container, deleted on teardown.
/// </summary>
public sealed class AzureBlobFixture : IAsyncLifetime
{
    private const string ConnectionEnvVar = "RESOURCEFULL_TEST_AZUREBLOB";

    /// <summary>Whether an account was named and the test container was created.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The client the tests reach the account with.</summary>
    public BlobServiceClient Client { get; private set; } = null!;

    /// <summary>The run's blob container.</summary>
    public string ContainerName { get; } = $"resourcefull-it-{Guid.NewGuid():N}"[..40];

    /// <summary>Skips the calling test, rather than failing it, when no account is named.</summary>
    public void SkipIfUnavailable() =>
        Assert.SkipUnless(IsAvailable, $"No Azure Storage account named. Set {ConnectionEnvVar} to run the Azure Blob tests.");

    public async ValueTask InitializeAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable(ConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        Client = new BlobServiceClient(connectionString);
        await WaitForAccountAsync();
        await Client.CreateBlobContainerAsync(ContainerName);
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (IsAvailable) await Client.DeleteBlobContainerAsync(ContainerName);
    }

    /// <summary>
    /// Builds a fully wired Resourcefull service provider on the test container, under a key prefix of its own.
    /// </summary>
    public ServiceProvider BuildServiceProvider(Action<ResourcefullOptionsBuilder>? configure = null, string keyPrefix = "resourcefull/")
    {
        var services = new ServiceCollection();
        services.AddResourcefull(options =>
        {
            options.UseAzureBlobStorage(ContainerName, azure =>
            {
                azure.Client = Client;
                azure.KeyPrefix = keyPrefix;
            });
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    private async Task WaitForAccountAsync()
    {
        // An emulator just started in a Docker container needs time to accept requests.
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await Client.GetPropertiesAsync();
                return;
            }
            catch (Exception exception) when (exception is RequestFailedException or HttpRequestException or AggregateException
                && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AzureBlobCollection : ICollectionFixture<AzureBlobFixture>
{
    public const string Name = "AzureBlob";
}
