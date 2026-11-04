using Azure.Storage.Blobs.Models;
using Resourcefull.AzureBlob;
using Resourcefull.ObjectStorage;
using Resourcefull.Tests.ObjectStorage;

namespace Resourcefull.Tests.AzureBlob;

[Collection(AzureBlobCollection.Name)]
public sealed class AzureBlobSessionJoinTests(AzureBlobFixture fixture) : ObjectStoreSessionJoinTests
{
    protected override void SkipIfUnavailable() => fixture.SkipIfUnavailable();

    private protected override IObjectStore NewStore() =>
        new AzureBlobObjectStore(fixture.Client.GetBlobContainerClient(fixture.ContainerName));

    protected override async Task<IReadOnlyList<string>> ListKeysAsync(string prefix)
    {
        var keys = new List<string>();
        await foreach (BlobItem blob in fixture.Client.GetBlobContainerClient(fixture.ContainerName)
            .GetBlobsAsync(new GetBlobsOptions { Prefix = prefix }))
            keys.Add(blob.Name);
        return keys;
    }
}
