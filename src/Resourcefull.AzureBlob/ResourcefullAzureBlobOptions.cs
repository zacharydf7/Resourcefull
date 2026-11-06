using Azure.Storage.Blobs;

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
