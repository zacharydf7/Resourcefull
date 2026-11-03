using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Resourcefull.ObjectStorage;

namespace Resourcefull.AzureBlob;

/// <summary>An Azure Blob Storage container, as <see cref="ObjectStoreRepository"/> uses it.</summary>
internal sealed class AzureBlobObjectStore(BlobContainerClient container) : IObjectStore
{
    /// <summary>How many blobs are deleted in parallel.</summary>
    private const int ParallelDeletes = 16;

    public string ConditionalWrites => "If-Match and If-None-Match on Put Blob";

    public async Task<(byte[]? Body, string? ETag)> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            Response<BlobDownloadResult> response = await container.GetBlobClient(key).DownloadContentAsync(cancellationToken)
                .ConfigureAwait(false);
            return (response.Value.Content.ToArray(), response.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException exception) when (IsMissingBlob(exception))
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Whether the store answered that the blob doesn't exist, rather than that the container doesn't, which is a
    /// mistake in the configuration and mustn't read as an empty store.
    /// </summary>
    private static bool IsMissingBlob(RequestFailedException exception) => exception.ErrorCode == BlobErrorCode.BlobNotFound;

    public async Task<bool> TryPutAsync(string key, byte[] body, string? etag)
    {
        var conditions = etag is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = new ETag(etag) };
        try
        {
            await container.GetBlobClient(key).UploadAsync(BinaryData.FromBytes(body), new BlobUploadOptions { Conditions = conditions },
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status is (int)HttpStatusCode.PreconditionFailed || exception.ErrorCode == BlobErrorCode.BlobAlreadyExists)
        {
            return false;
        }
    }

    /// <summary>
    /// Content of a part or more goes up as blocks, one per part, then a block list that commits them. If the upload
    /// fails, Azure discards the uncommitted blocks on its own, after a week.
    /// </summary>
    public async Task UploadAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ObjectParts.PartSize);
        try
        {
            int filled = await ObjectParts.FillAsync(content, buffer, cancellationToken).ConfigureAwait(false);
            BlockBlobClient blob = container.GetBlockBlobClient(key);
            if (filled < ObjectParts.PartSize)
            {
                // Each version's content has a key of its own, so a resend after a lost answer may overwrite it.
                await blob.UploadAsync(new MemoryStream(buffer, 0, filled, false), new BlobUploadOptions(), cancellationToken).ConfigureAwait(false);
                return;
            }

            var blocks = new List<string>();
            for (int number = 1; filled > 0; number++)
            {
                // Azure requires every block ID in a blob to be the same length.
                string id = Convert.ToBase64String(Encoding.ASCII.GetBytes(number.ToString("D6")));
                await blob.StageBlockAsync(id, new MemoryStream(buffer, 0, filled, false), cancellationToken: cancellationToken).ConfigureAwait(false);
                blocks.Add(id);
                filled = await ObjectParts.FillAsync(content, buffer, cancellationToken).ConfigureAwait(false);
            }
            await blob.CommitBlockListAsync(blocks, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<ObjectRead?> OpenReadAsync(string key, long start, long end, long length, CancellationToken cancellationToken)
    {
        var options = new BlobDownloadOptions();
        if (start > 0 || end < length) options.Range = new HttpRange(start, end - start);
        try
        {
            Response<BlobDownloadStreamingResult> response = await container.GetBlobClient(key).DownloadStreamingAsync(options, cancellationToken)
                .ConfigureAwait(false);
            return new ObjectRead(response.Value.Content, response.Value);
        }
        catch (RequestFailedException exception) when (IsMissingBlob(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Lists from <paramref name="startAfter"/> with <c>startFrom</c>, which is inclusive, so the blob named
    /// <paramref name="startAfter"/>, if there is one, is skipped.
    /// </summary>
    public async IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (BlobItem item in container.GetBlobsAsync(new GetBlobsOptions { Prefix = prefix, StartFrom = startAfter }, cancellationToken)
            .ConfigureAwait(false))
        {
            if (item.Name == startAfter) continue;
            yield return new ObjectListing(item.Name, item.Properties.ContentLength ?? 0);
        }
    }

    public Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = ParallelDeletes, CancellationToken = cancellationToken },
            async (key, ct) => await container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false));

    /// <remarks>
    /// Once its retries run out, the Azure SDK throws an <see cref="AggregateException"/> holding each try's failure,
    /// such as a <see cref="RequestFailedException"/> for a store it couldn't reach.
    /// </remarks>
    public bool IsStoreFailure(Exception exception) => exception is AggregateException aggregate
        ? aggregate.InnerExceptions.All(IsStoreFailure)
        : exception is RequestFailedException or HttpRequestException or IOException or TimeoutException or OperationCanceledException;
}
