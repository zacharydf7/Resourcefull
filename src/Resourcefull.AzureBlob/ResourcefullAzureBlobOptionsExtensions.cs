using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Resourcefull.ObjectStorage;

namespace Resourcefull.AzureBlob;

/// <summary>
/// How Resourcefull reaches Azure Blob Storage.
/// <para>Coming in 4.0.</para>
/// </summary>
public sealed class ResourcefullAzureBlobOptions
{
    /// <summary>
    /// The client to reach the storage account with, such as <c>new BlobServiceClient(connectionString)</c>. When
    /// <see langword="null"/>, the <see cref="BlobServiceClient"/> registered in the application's services is used.
    /// </summary>
    public BlobServiceClient? Client { get; set; }

    /// <summary>
    /// What the name of every blob Resourcefull writes starts with, so it can share a container: <c>resourcefull/</c> by
    /// default. Empty keeps everything at the container's top level.
    /// </summary>
    public string KeyPrefix { get; set; } = "resourcefull/";
}

/// <summary>
/// Configures Resourcefull to store resources in Azure Blob Storage.
/// <para>Coming in 4.0.</para>
/// </summary>
public static class ResourcefullAzureBlobOptionsExtensions
{
    /// <summary>
    /// The longest key prefix. A blob's name can be 1,024 characters long; an escaped name takes up to 773 of them, and
    /// the container's ID, the separators and a metadata search entry's token another 60.
    /// </summary>
    internal const int MaxKeyPrefixLength = 190;

    /// <summary>
    /// Stores resources in the blob container <paramref name="containerName"/>, which must exist. Every Resourcefull
    /// container is kept in it, under the key prefix, so Resourcefull's container names aren't limited by Azure's rules
    /// for blob container names.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddResourcefull</c>.</param>
    /// <param name="containerName">The blob container that holds Resourcefull's blobs.</param>
    /// <param name="configure">Sets the client and the key prefix; <see langword="null"/> keeps the defaults.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// A provider has already been configured; or, when Resourcefull is first used, no client was set and none is
    /// registered.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="containerName"/> is null, empty or whitespace (<see cref="ArgumentNullException"/> when null), or
    /// the key prefix is null or longer than 190 characters.
    /// </exception>
    public static ResourcefullOptionsBuilder UseAzureBlobStorage(this ResourcefullOptionsBuilder builder, string containerName,
        Action<ResourcefullAzureBlobOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        var options = new ResourcefullAzureBlobOptions();
        configure?.Invoke(options);
        ArgumentNullException.ThrowIfNull(options.KeyPrefix, nameof(configure));
        if (options.KeyPrefix.Length > MaxKeyPrefixLength)
            throw new ArgumentException($"The key prefix can be at most {MaxKeyPrefixLength} characters.", nameof(configure));

        string? storeName = builder.StoreName;
        // The repository holds no state of its own, and the client is thread-safe, so one instance serves every scope.
        builder.UseProvider(services =>
        {
            BlobServiceClient client = options.Client ?? services.GetService<BlobServiceClient>() ?? throw new InvalidOperationException(
                "UseAzureBlobStorage needs a client: set Client, for example to new BlobServiceClient(connectionString), or register a BlobServiceClient in the application's services.");
            return new ObjectStoreRepository(new AzureBlobObjectStore(client.GetBlobContainerClient(containerName)), options.KeyPrefix,
                services.GetService<TimeProvider>(), storeName, services.SearchableMetadataKeys(storeName));
        }, ServiceLifetime.Singleton);
        return builder;
    }
}
