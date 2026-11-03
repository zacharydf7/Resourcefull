using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Resourcefull.ObjectStorage;

namespace Resourcefull.S3;

/// <summary>An S3-compatible bucket, as <see cref="ObjectStoreRepository"/> uses it.</summary>
internal sealed class S3ObjectStore(IAmazonS3 s3, string bucket) : IObjectStore
{
    /// <summary>The most keys one <c>DeleteObjects</c> request takes.</summary>
    private const int DeleteBatchSize = 1000;

    /// <summary>How many parts a join copies in parallel.</summary>
    private const int ParallelCopies = 16;

    public string ConditionalWrites => "If-Match and If-None-Match on PutObject";

    public async Task<(byte[]? Body, string? ETag)> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using GetObjectResponse response = await s3.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            using var body = new MemoryStream();
            await response.ResponseStream.CopyToAsync(body, cancellationToken).ConfigureAwait(false);
            return (body.ToArray(), response.ETag);
        }
        catch (AmazonS3Exception exception) when (IsMissingObject(exception))
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Whether the store answered that the object doesn't exist, rather than that the bucket doesn't, which is a
    /// mistake in the configuration and mustn't read as an empty store. A key too long to exist counts as missing.
    /// </summary>
    private static bool IsMissingObject(AmazonS3Exception exception) =>
        exception is { StatusCode: HttpStatusCode.NotFound, ErrorCode: not "NoSuchBucket" } or { ErrorCode: "KeyTooLongError" };

    public async Task<bool> TryPutAsync(string key, byte[] body, string? etag)
    {
        var request = new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body, false) };
        if (etag is null) request.IfNoneMatch = "*";
        else request.IfMatch = etag;
        try
        {
            await s3.PutObjectAsync(request, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    /// <summary>Content of a part or more goes up as a multipart upload, abandoned if it fails.</summary>
    public async Task UploadAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ObjectParts.PartSize);
        try
        {
            int filled = await ObjectParts.FillAsync(content, buffer, cancellationToken).ConfigureAwait(false);
            if (filled < ObjectParts.PartSize)
            {
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket, Key = key, InputStream = new MemoryStream(buffer, 0, filled, false)
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            InitiateMultipartUploadResponse upload = await s3.InitiateMultipartUploadAsync(
                new InitiateMultipartUploadRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
            try
            {
                var parts = new List<PartETag>();
                for (int number = 1; filled > 0; number++)
                {
                    UploadPartResponse part = await s3.UploadPartAsync(new UploadPartRequest
                    {
                        BucketName = bucket, Key = key, UploadId = upload.UploadId, PartNumber = number,
                        InputStream = new MemoryStream(buffer, 0, filled, false), PartSize = filled
                    }, cancellationToken).ConfigureAwait(false);
                    parts.Add(new PartETag(number, part.ETag));
                    filled = await ObjectParts.FillAsync(content, buffer, cancellationToken).ConfigureAwait(false);
                }
                await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = bucket, Key = key, UploadId = upload.UploadId, PartETags = parts
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await AbortQuietlyAsync(key, upload.UploadId).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>A multipart upload's limit on its parts.</summary>
    public int MaxJoinedParts => 10_000;

    /// <summary>A multipart upload whose parts are copied from the objects with <c>UploadPartCopy</c>.</summary>
    public async Task JoinAsync(string key, IReadOnlyList<string> parts, CancellationToken cancellationToken)
    {
        if (parts.Count == 0)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream() },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        InitiateMultipartUploadResponse upload = await s3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        try
        {
            var copied = new PartETag[parts.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, parts.Count),
                new ParallelOptions { MaxDegreeOfParallelism = ParallelCopies, CancellationToken = cancellationToken }, async (i, ct) =>
                {
                    CopyPartResponse part = await s3.CopyPartAsync(new CopyPartRequest
                    {
                        SourceBucket = bucket, SourceKey = parts[i], DestinationBucket = bucket, DestinationKey = key,
                        UploadId = upload.UploadId, PartNumber = i + 1
                    }, ct).ConfigureAwait(false);
                    copied[i] = new PartETag(i + 1, part.ETag);
                }).ConfigureAwait(false);
            await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = key, UploadId = upload.UploadId, PartETags = [.. copied]
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortQuietlyAsync(key, upload.UploadId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task AbortQuietlyAsync(string key, string uploadId)
    {
        try
        {
            await s3.AbortMultipartUploadAsync(bucket, key, uploadId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            // Amazon S3 keeps the parts until a lifecycle rule aborts the upload; some other stores clean them up on
            // their own.
        }
    }

    public async Task<ObjectRead?> OpenReadAsync(string key, long start, long end, long length, CancellationToken cancellationToken)
    {
        var request = new GetObjectRequest { BucketName = bucket, Key = key };
        if (start > 0 || end < length) request.ByteRange = new ByteRange(start, end - 1);
        try
        {
            GetObjectResponse response = await s3.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return new ObjectRead(response.ResponseStream, response);
        }
        catch (AmazonS3Exception exception) when (IsMissingObject(exception))
        {
            return null;
        }
    }

    public async IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, StartAfter = startAfter };
        ListObjectsV2Response response;
        do
        {
            response = await s3.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (S3Object item in response.S3Objects ?? [])
                yield return new ObjectListing(item.Key, item.Size ?? 0, DateTime.SpecifyKind(item.LastModified ?? default, DateTimeKind.Utc));
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    public async Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        foreach (string[] batch in keys.Chunk(DeleteBatchSize))
        {
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket, Objects = [.. batch.Select(k => new KeyVersion { Key = k })]
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool IsStoreFailure(Exception exception) =>
        exception is AmazonServiceException or HttpRequestException or IOException or TimeoutException or OperationCanceledException;
}
