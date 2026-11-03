using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Resourcefull.ObjectStorage;

namespace Resourcefull.AzureBlob;

/// <summary>An Azure Blob Storage container, as <see cref="ObjectStoreRepository"/> uses it.</summary>
internal sealed class AzureBlobObjectStore(BlobContainerClient container) : IObjectStore
{
    /// <summary>How many blobs are deleted, or copied into a join, in parallel.</summary>
    private const int ParallelRequests = 16;

    /// <summary>
    /// How long the read-only link a join signs stays valid: long enough for the largest join, which is safe since the
    /// link goes only to Azure.
    /// </summary>
    private static readonly TimeSpan JoinReadTime = TimeSpan.FromDays(1);

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
                string id = BlockId(number);
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

    /// <summary>A block ID: Azure requires every block ID in a blob to be the same length.</summary>
    private static string BlockId(int number) => Convert.ToBase64String(Encoding.ASCII.GetBytes(number.ToString("D6")));

    /// <summary>A block blob's limit on its blocks.</summary>
    public int MaxJoinedParts => 50_000;

    /// <summary>
    /// Blocks staged with <c>Put Block From URL</c>, each from a part, then a block list that commits them. Azure reads
    /// the parts through a read-only link the store signs.
    /// </summary>
    public async Task JoinAsync(string key, IReadOnlyList<ObjectRange> parts, CancellationToken cancellationToken)
    {
        BlockBlobClient blob = container.GetBlockBlobClient(key);
        if (parts.Count == 0)
        {
            await blob.UploadAsync(new MemoryStream(), new BlobUploadOptions(), cancellationToken).ConfigureAwait(false);
            return;
        }

        Func<string, Uri> readable = await SignReadsAsync(cancellationToken).ConfigureAwait(false);
        string[] blocks = [.. parts.Select((_, i) => BlockId(i + 1))];
        await Parallel.ForEachAsync(Enumerable.Range(0, parts.Count),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelRequests, CancellationToken = cancellationToken },
            async (i, ct) => await blob.StageBlockFromUriAsync(readable(parts[i].Key), blocks[i],
                new StageBlockFromUriOptions { SourceRange = new HttpRange(parts[i].Offset, parts[i].Length) }, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
        await blob.CommitBlockListAsync(blocks, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What makes a link to read a blob: the blob's URL, with the client's SAS, when the client's URL carries one; a link
    /// signed with the account key when the client has one; and otherwise one signed with a user delegation key, which a
    /// client signed in with Microsoft Entra ID asks for.
    /// </summary>
    private async Task<Func<string, Uri>> SignReadsAsync(CancellationToken cancellationToken)
    {
        if (new BlobUriBuilder(container.Uri).Sas?.Signature is { Length: > 0 }) return name => container.GetBlobClient(name).Uri;
        DateTimeOffset expiresOn = DateTimeOffset.UtcNow + JoinReadTime;
        if (container.CanGenerateSasUri) return name => container.GetBlobClient(name).GenerateSasUri(BlobSasPermissions.Read, expiresOn);

        BlobServiceClient service = container.GetParentBlobServiceClient();
        UserDelegationKey key = (await service.GetUserDelegationKeyAsync(null, expiresOn, cancellationToken).ConfigureAwait(false)).Value;
        return name =>
        {
            var sas = new BlobSasBuilder(BlobSasPermissions.Read, expiresOn)
            {
                BlobContainerName = container.Name, BlobName = name, Resource = "b"
            };
            return new BlobUriBuilder(container.GetBlobClient(name).Uri) { Sas = sas.ToSasQueryParameters(key, service.AccountName) }
                .ToUri();
        };
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
            yield return new ObjectListing(item.Name, item.Properties.ContentLength ?? 0, item.Properties.LastModified ?? default);
        }
    }

    public Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = ParallelRequests, CancellationToken = cancellationToken },
            async (key, ct) => await container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: ct).ConfigureAwait(false));

    /// <remarks>
    /// Once its retries run out, the Azure SDK throws an <see cref="AggregateException"/> holding each try's failure,
    /// such as a <see cref="RequestFailedException"/> for a store it couldn't reach.
    /// </remarks>
    public bool IsStoreFailure(Exception exception) => exception is AggregateException aggregate
        ? aggregate.InnerExceptions.All(IsStoreFailure)
        : exception is RequestFailedException or HttpRequestException or IOException or TimeoutException or OperationCanceledException;
}
