using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Resourcefull.Providers;

namespace Resourcefull.S3;

/// <summary>
/// Keeps resources in an S3-compatible bucket, using only conditional puts for consistency. Each container has a
/// record, and each name a head object listing its versions' attributes in the order they were stored, the last one
/// current; every change to a name is a compare-and-swap of its head, retried when another writer got there first.
/// Content goes in objects of its own, written before the head names them. See <see cref="S3Keys"/> for the layout.
/// </summary>
/// <remarks>
/// Every conditional write carries a random write ID. When the store refuses one, the object is read again: if it
/// holds that ID, the write did land, and the refusal was the SDK sending it again after its answer was lost.
/// </remarks>
internal sealed class ResourceS3Repository(IAmazonS3 s3, string bucket, string keyPrefix, TimeProvider? timeProvider,
    string? storeName) : ResourceRepository
{
    /// <summary>
    /// Content smaller than this goes up in one request; the rest goes in parts of this size, so memory stays bounded.
    /// </summary>
    internal const int PartSize = 8 * 1024 * 1024;

    /// <summary>
    /// The most one read of the content asks for. An upload inspector's refusal stops the upload at the next read, so
    /// reading a part in small slices keeps the upload from reading content it then throws away.
    /// </summary>
    private const int ReadSize = 81920;

    /// <summary>How many heads or records a listing reads at once.</summary>
    private const int ParallelReads = 16;

    /// <summary>How many names <see cref="GetAllResourceInfoAsync"/> lists at a time.</summary>
    private const int ListAllPageSize = 1000;

    /// <summary>
    /// How many times in a row a conditional write may be refused before it fails: a store that keeps refusing one
    /// most likely doesn't honor the conditions at all.
    /// </summary>
    private const int MaxAttempts = 100;

    /// <summary>
    /// The size of the head a name keeps once all its versions are deleted: just the write ID. A head with versions is
    /// JSON, and always larger.
    /// </summary>
    private const int EmptyHeadSize = 32;

    private readonly S3Keys _keys = new(keyPrefix);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // Resources

    public override Task<WrittenContent> AddResourceAsync(NewResourceVersion version, Stream content,
        ResourceWriteCondition? condition = null, CancellationToken cancellationToken = default) =>
        AddVersionAsync(version, content, condition ?? new ResourceWriteCondition(), null, cancellationToken);

    public override Task<WrittenContent> ImportVersionAsync(NewResourceVersion version, Stream content,
        CancellationToken cancellationToken = default) =>
        AddVersionAsync(version, content, new ResourceWriteCondition(), version.LastModified, cancellationToken);

    /// <summary>
    /// Stores a new version as the name's current one: an upload, stamped with the time it becomes current, or an
    /// import, stamped with <paramref name="lastModified"/>, the time it was first stored.
    /// </summary>
    private async Task<WrittenContent> AddVersionAsync(NewResourceVersion version, Stream content, ResourceWriteCondition condition,
        DateTimeOffset? lastModified, CancellationToken cancellationToken)
    {
        ResourceLocation location = version.Location;
        ContainerRecord container = await GetContainerAsync(location.ContainerName, cancellationToken).ConfigureAwait(false)
            ?? throw ContainerMissing(location.ContainerName);
        string headKey = _keys.Head(container.Id, location.ResourceName);

        // Checked again by the head write; this only spares uploading content that would be refused.
        if (condition.CreateOnly || condition.Conditions is not null)
        {
            Head? existing = (await GetAsync<Head>(headKey, cancellationToken).ConfigureAwait(false)).Value;
            ThrowUnlessAddable(location, existing, version.VersionId, condition);
        }

        string blob = Guid.NewGuid().ToString("N");
        string contentKey = _keys.Content(container.Id, blob);
        using var hashing = new HashingStream(content);
        WrittenContent? written = null;
        try
        {
            await UploadAsync(contentKey, hashing, cancellationToken).ConfigureAwait(false);
            await ChangeHeadAsync(headKey, head =>
            {
                // An earlier try already added this version: it landed, but another writer changed the head before it
                // could be confirmed.
                if (head?.Versions.Any(v => v.Blob == blob) == true) return null;
                ThrowUnlessAddable(location, head, version.VersionId, condition);
                // Stamped as the version becomes current. A wrong declared length throws here, before the head names it.
                written = hashing.Complete(version.DeclaredLength, lastModified ?? _clock.GetUtcNow());
                return new Head([.. head?.Versions ?? [], new StoredVersion(version.VersionId, blob, written.ContentLength,
                    written.ContentHash, version.ContentType, new Dictionary<string, string>(version.Metadata),
                    written.LastModified, version.ExpiresAt, version.CopiedFrom)]);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DeleteUnnamedContentAsync(headKey, blob, contentKey).ConfigureAwait(false);
            throw;
        }

        // A container deleted while the version was stored: the head went in under the container's old ID, which
        // nothing reads anymore, so whatever is left there is deleted.
        if ((await GetContainerAsync(location.ContainerName, CancellationToken.None).ConfigureAwait(false))?.Id != container.Id)
        {
            await PurgeQuietlyAsync(container.Id).ConfigureAwait(false);
            throw ContainerMissing(location.ContainerName);
        }
        return written!;
    }

    private static void ThrowUnlessAddable(ResourceLocation location, Head? head, string versionId, ResourceWriteCondition condition)
    {
        StoredVersion? current = head?.Versions[^1];
        if (condition.CreateOnly && current is not null)
        {
            throw new ResourceConflictException(
                $"The resource {location.ResourceName} already exists in the container {location.ContainerName}.")
            {
                ContainerName = location.ContainerName, ResourceName = location.ResourceName
            };
        }
        if (head?.Versions.Any(v => v.VersionId == versionId) == true)
        {
            throw new ResourceConflictException($"The resource {location.ResourceName} in the container " +
                $"{location.ContainerName} already has a version {versionId}.")
            {
                ContainerName = location.ContainerName, ResourceName = location.ResourceName
            };
        }
        condition.Conditions?.ThrowIfNotMet(location, current?.VersionId, current?.ContentHash);
    }

    /// <summary>
    /// Deletes the content of a write that failed, unless the head names it after all, as it does when the head write
    /// landed and what failed came after. If the head can't be read, the content stays.
    /// </summary>
    private async Task DeleteUnnamedContentAsync(string headKey, string blob, string contentKey)
    {
        try
        {
            Head? head = (await GetAsync<Head>(headKey, CancellationToken.None).ConfigureAwait(false)).Value;
            if (head?.Versions.Any(v => v.Blob == blob) != true) await DeleteQuietlyAsync([contentKey]).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            // Left in place: deleting content a head names would lose a stored version.
        }
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetAllResourceInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        var all = new List<ResourceInfo>();
        IReadOnlyList<ResourceInfo> page;
        do
        {
            page = await GetResourcePageAsync(resourceContainerName, null, all.LastOrDefault()?.Name, ListAllPageSize, cancellationToken)
                .ConfigureAwait(false);
            all.AddRange(page);
        }
        while (page.Count == ListAllPageSize);
        return all;
    }

    /// <summary>Lists the names' heads in key order, which is their names' code point order, and reads them.</summary>
    public override async Task<IReadOnlyList<ResourceInfo>> GetResourcePageAsync(string resourceContainerName, string? prefix,
        string? afterName, int pageSize, CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is not { } container)
            return [];

        string heads = _keys.Heads(container.Id);
        var page = new List<ResourceInfo>();
        await foreach (string[] keys in ListKeysAsync(_keys.HeadsStartingWith(container.Id, prefix),
            afterName is null ? null : _keys.Head(container.Id, afterName), item => item.Size > EmptyHeadSize,
            () => pageSize - page.Count, cancellationToken).ConfigureAwait(false))
        {
            foreach ((string key, Head? head) in await ReadAllAsync<Head>(keys, cancellationToken).ConfigureAwait(false))
            {
                // A name whose versions were deleted since it was listed is left out.
                if (head is not null)
                    page.Add(ToInfo(resourceContainerName, S3Keys.Unescape(key[heads.Length..]), head.Versions[^1], true));
            }
            if (page.Count >= pageSize) break;
        }
        return page;
    }

    public override async Task<IReadOnlyList<ResourceInfo>> GetResourceVersionsAsync(ResourceLocation resource,
        CancellationToken cancellationToken = default)
    {
        if (await GetHeadAsync(resource, cancellationToken).ConfigureAwait(false) is not { } head) return [];
        return [.. head.Versions.Select((v, i) => ToInfo(resource.ContainerName, resource.ResourceName, v, i == head.Versions.Count - 1))
            .Reverse()];
    }

    public override async Task<ResourceInfo?> GetResourceInfoAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        if (await GetHeadAsync(resource, cancellationToken).ConfigureAwait(false) is not { } head) return null;
        StoredVersion current = head.Versions[^1];
        StoredVersion? version = versionId is null ? current : head.Versions.FirstOrDefault(v => v.VersionId == versionId);
        return version is null ? null : ToInfo(resource.ContainerName, resource.ResourceName, version, version == current);
    }

    public override Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId = null,
        CancellationToken cancellationToken = default) =>
        GetResourceStreamAsync(resource, versionId, 0, null, cancellationToken);

    /// <summary>Opens a version's content from <paramref name="offset"/>, asking the store for only the range read.</summary>
    public override async Task<Stream?> GetResourceStreamAsync(ResourceLocation resource, string? versionId, long offset,
        long? length, CancellationToken cancellationToken = default)
    {
        ContainerRecord? container = await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false);
        if (container is null) return null;
        Head? head = (await GetAsync<Head>(_keys.Head(container.Id, resource.ResourceName), cancellationToken).ConfigureAwait(false)).Value;
        StoredVersion? version = versionId is null ? head?.Versions[^1] : head?.Versions.FirstOrDefault(v => v.VersionId == versionId);
        if (version is null) return null;

        long start = Math.Min(offset, version.ContentLength);
        long end = Math.Min(length is { } count ? start + count : version.ContentLength, version.ContentLength);
        if (start >= end) return new S3ResourceStream(null, version.ContentLength, start);

        var request = new GetObjectRequest { BucketName = bucket, Key = _keys.Content(container.Id, version.Blob) };
        if (start > 0 || end < version.ContentLength) request.ByteRange = new ByteRange(start, end - 1);
        try
        {
            GetObjectResponse response = await s3.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return new S3ResourceStream(response, version.ContentLength, start);
        }
        catch (AmazonS3Exception exception) when (IsMissingObject(exception))
        {
            // Deleted since the head was read.
            return null;
        }
    }

    /// <summary>
    /// Takes the versions off the name's head in one write, deciding the conditions there, then deletes their content.
    /// Content whose delete fails is no longer named by any head, and stays until the container is deleted.
    /// </summary>
    public override async Task<bool> DeleteResourceAsync(ResourceLocation resource, string? versionId = null,
        ResourceConditions? conditions = null, CancellationToken cancellationToken = default)
    {
        ContainerRecord? container = await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            conditions?.ThrowIfNotMet(resource, null, null);
            return false;
        }

        var removed = new List<StoredVersion>();
        bool changed = await ChangeHeadAsync(_keys.Head(container.Id, resource.ResourceName), head =>
        {
            removed.Clear();
            StoredVersion? current = head?.Versions[^1];
            conditions?.ThrowIfNotMet(resource, current?.VersionId, current?.ContentHash);
            if (head is null) return null;
            removed.AddRange(versionId is null ? head.Versions : head.Versions.Where(v => v.VersionId == versionId));
            return removed.Count == 0 ? null : new Head([.. head.Versions.Except(removed)]);
        }, cancellationToken).ConfigureAwait(false);
        if (!changed) return false;

        await DeleteQuietlyAsync([.. removed.Select(v => _keys.Content(container.Id, v.Blob))]).ConfigureAwait(false);
        return true;
    }

    public override Task<bool> SetResourceMetadataAsync(ResourceLocation resource, string? versionId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default) =>
        ChangeVersionAsync(resource, versionId, v => v with { Metadata = new Dictionary<string, string>(metadata) }, cancellationToken);

    public override Task<bool> SetContentTypeAsync(ResourceLocation resource, string? versionId, string? contentType,
        CancellationToken cancellationToken = default) =>
        ChangeVersionAsync(resource, versionId, v => v with { ContentType = contentType }, cancellationToken);

    private async Task<bool> ChangeVersionAsync(ResourceLocation resource, string? versionId, Func<StoredVersion, StoredVersion> change,
        CancellationToken cancellationToken)
    {
        if (await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is not { } container) return false;
        return await ChangeHeadAsync(_keys.Head(container.Id, resource.ResourceName), head =>
        {
            int index = head is null ? -1 : versionId is null ? head.Versions.Count - 1 : head.Versions.FindIndex(v => v.VersionId == versionId);
            if (index < 0) return null;
            List<StoredVersion> versions = [.. head!.Versions];
            versions[index] = change(versions[index]);
            return new Head(versions);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds up the container's content objects' sizes, and counts the names with versions, from listings alone.
    /// Content an unfinished write left behind counts until the container is deleted.
    /// </summary>
    public override async Task<ResourceContainerUsage> GetContainerUsageAsync(string resourceContainerName,
        CancellationToken cancellationToken = default)
    {
        if (await GetContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is not { } container)
            return new ResourceContainerUsage(0, 0, 0);

        long storedBytes = 0, versions = 0, resources = 0;
        await foreach (S3Object content in ListAsync(_keys.Contents(container.Id), null, cancellationToken).ConfigureAwait(false))
        {
            storedBytes += content.Size ?? 0;
            versions++;
        }
        await foreach (S3Object head in ListAsync(_keys.Heads(container.Id), null, cancellationToken).ConfigureAwait(false))
        {
            if (head.Size > EmptyHeadSize) resources++;
        }
        return new ResourceContainerUsage(storedBytes, resources, versions);
    }

    // Resource Containers

    public override async Task AddContainerAsync(string resourceContainerName, IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default)
    {
        string key = _keys.Container(resourceContainerName);
        var record = new ContainerRecord(Guid.NewGuid().ToString("N"), new Dictionary<string, string>(metadata));
        for (int attempt = 1; ; attempt++)
        {
            Stored<ContainerRecord> existing = await GetAsync<ContainerRecord>(key, cancellationToken).ConfigureAwait(false);
            // An earlier try already created it: it landed, but another writer changed the record before it could be
            // confirmed.
            if (existing.Value?.Id == record.Id) return;
            if (existing.Value is { Deleted: false })
            {
                throw new ResourceConflictException($"The container {resourceContainerName} already exists.")
                {
                    ContainerName = resourceContainerName
                };
            }
            // A deleted container's record holds the only note of its old ID, so what's left under it is deleted first.
            if (existing.Value is { } deleted) await PurgeAsync(deleted.Id, cancellationToken).ConfigureAwait(false);
            if (await TryPutAsync(key, id => Serialize(record with { WriteId = id }), existing.ETag).ConfigureAwait(false)) return;
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<IReadOnlyList<ContainerInfo>> GetContainerPageAsync(string? prefix, string? afterName, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = new List<ContainerInfo>();
        await foreach (string[] keys in ListKeysAsync(_keys.ContainersStartingWith(prefix),
            afterName is null ? null : _keys.Container(afterName), _ => true, () => pageSize - page.Count, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach ((string key, ContainerRecord? record) in await ReadAllAsync<ContainerRecord>(keys, cancellationToken).ConfigureAwait(false))
            {
                if (record is { Deleted: false })
                    page.Add(ToInfo(S3Keys.Unescape(key[_keys.Containers.Length..]), record));
            }
            if (page.Count >= pageSize) break;
        }
        return page;
    }

    public override async Task<ContainerInfo?> GetContainerInfoAsync(string resourceContainerName,
        CancellationToken cancellationToken = default) =>
        await GetContainerAsync(resourceContainerName, cancellationToken).ConfigureAwait(false) is { } record
            ? ToInfo(resourceContainerName, record)
            : null;

    /// <summary>
    /// Marks the container's record deleted, which hides everything in it at once, then deletes what it held. If that
    /// stops partway, deleting the container again finishes it.
    /// </summary>
    public override async Task<bool> DeleteContainerAsync(string resourceContainerName, CancellationToken cancellationToken = default)
    {
        string key = _keys.Container(resourceContainerName);
        string? deleting = null;
        for (int attempt = 1; ; attempt++)
        {
            Stored<ContainerRecord> existing = await GetAsync<ContainerRecord>(key, cancellationToken).ConfigureAwait(false);
            if (existing.Value is not { } record) return false;
            // An earlier try already marked it deleted: it landed, but before that could be confirmed, the container was
            // created again under a new ID, which mustn't be deleted.
            if (deleting is not null && record.Id != deleting)
            {
                await PurgeAsync(deleting, cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (record.Deleted)
            {
                await PurgeAsync(record.Id, cancellationToken).ConfigureAwait(false);
                return false;
            }
            deleting = record.Id;
            if (await TryPutAsync(key, id => Serialize(record with { Deleted = true, WriteId = id }), existing.ETag).ConfigureAwait(false))
            {
                await PurgeAsync(record.Id, cancellationToken).ConfigureAwait(false);
                return true;
            }
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<bool> SetContainerMetadataAsync(string resourceContainerName,
        IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken = default)
    {
        string key = _keys.Container(resourceContainerName);
        for (int attempt = 1; ; attempt++)
        {
            Stored<ContainerRecord> existing = await GetAsync<ContainerRecord>(key, cancellationToken).ConfigureAwait(false);
            if (existing.Value is not { Deleted: false } record) return false;
            ContainerRecord changed = record with { Metadata = new Dictionary<string, string>(metadata) };
            if (await TryPutAsync(key, id => Serialize(changed with { WriteId = id }), existing.ETag).ConfigureAwait(false)) return true;
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    // Storage

    private async Task<ContainerRecord?> GetContainerAsync(string resourceContainerName, CancellationToken cancellationToken) =>
        (await GetAsync<ContainerRecord>(_keys.Container(resourceContainerName), cancellationToken).ConfigureAwait(false)).Value
            is { Deleted: false } record ? record : null;

    private async Task<Head?> GetHeadAsync(ResourceLocation resource, CancellationToken cancellationToken) =>
        await GetContainerAsync(resource.ContainerName, cancellationToken).ConfigureAwait(false) is { } container
            ? (await GetAsync<Head>(_keys.Head(container.Id, resource.ResourceName), cancellationToken).ConfigureAwait(false)).Value
            : null;

    /// <summary>
    /// Changes a name's head with <paramref name="change"/>, which gets the head (or <see langword="null"/> for a name
    /// with no versions) and returns the new one, or <see langword="null"/> to change nothing. When another writer
    /// changes the head first, it reads it again and calls <paramref name="change"/> again.
    /// </summary>
    /// <returns>Whether the head changed.</returns>
    private async Task<bool> ChangeHeadAsync(string key, Func<Head?, Head?> change, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            Stored<Head> current = await GetAsync<Head>(key, cancellationToken).ConfigureAwait(false);
            if (change(current.Value) is not { } changed) return false;
            if (await TryPutAsync(key, id => Serialize(changed with { WriteId = id }), current.ETag).ConfigureAwait(false)) return true;

            S3Telemetry.CountRetry(storeName);
            await WaitToRetryAsync(key, attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits a little before writing <paramref name="key"/> again, at random once writers keep colliding, so one of them
    /// gets through; and gives up once the store has refused <see cref="MaxAttempts"/> writes in a row.
    /// </summary>
    private static async Task WaitToRetryAsync(string key, int attempt, CancellationToken cancellationToken)
    {
        if (attempt >= MaxAttempts)
        {
            throw new InvalidOperationException($"The store refused {MaxAttempts} conditional writes of {key} in a row. " +
                "Check that it honors If-Match and If-None-Match on PutObject, and that nothing else writes under Resourcefull's prefix.");
        }
        if (attempt > 2) await Task.Delay(Random.Shared.Next(1, Math.Min(10 * attempt, 200)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An object's value, and its ETag for a conditional write; both <see langword="null"/> when there's no object.
    /// A head with no versions, which holds only a write ID, has an ETag and no value.
    /// </summary>
    private async Task<Stored<T>> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        (byte[]? body, string? etag) = await GetBytesAsync(key, cancellationToken).ConfigureAwait(false);
        return new Stored<T>(body is { Length: > 0 } && body[0] == '{' ? JsonSerializer.Deserialize<T>(body) : null, etag);
    }

    private async Task<(byte[]? Body, string? ETag)> GetBytesAsync(string key, CancellationToken cancellationToken)
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

    /// <summary>
    /// Writes the body <paramref name="body"/> makes from a new write ID, only if the object is still as it was read:
    /// with the ETag it had, or absent when <paramref name="etag"/> is <see langword="null"/>. Once sent, a write isn't
    /// canceled, so a caller that cancels never leaves it unknown whether the write landed.
    /// </summary>
    /// <returns>Whether it was written; <see langword="false"/> when another writer changed the object first.</returns>
    private async Task<bool> TryPutAsync(string key, Func<string, byte[]> body, string? etag)
    {
        string writeId = Guid.NewGuid().ToString("N");
        var request = new PutObjectRequest { BucketName = bucket, Key = key, InputStream = new MemoryStream(body(writeId), false) };
        if (etag is null) request.IfNoneMatch = "*";
        else request.IfMatch = etag;
        try
        {
            await s3.PutObjectAsync(request, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            // The write may have landed and its response been lost, and the store then refused the SDK's resend because
            // of it.
            return await ReadWriteIdAsync(key).ConfigureAwait(false) == writeId;
        }
    }

    /// <summary>The write ID an object holds: in its JSON, or as the whole of a head with no versions.</summary>
    private async Task<string?> ReadWriteIdAsync(string key)
    {
        (byte[]? body, _) = await GetBytesAsync(key, CancellationToken.None).ConfigureAwait(false);
        if (body is null) return null;
        if (body.Length == 0 || body[0] != '{') return Encoding.UTF8.GetString(body);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("w", out JsonElement id) ? id.GetString() : null;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="key"/>, reading it until a read returns nothing: in one
    /// request when it fits in a part, otherwise as a multipart upload, abandoned if it fails.
    /// </summary>
    private async Task UploadAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PartSize);
        try
        {
            int filled = await FillAsync(content, buffer, cancellationToken).ConfigureAwait(false);
            if (filled < PartSize)
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
                    filled = await FillAsync(content, buffer, cancellationToken).ConfigureAwait(false);
                }
                await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
                {
                    BucketName = bucket, Key = key, UploadId = upload.UploadId, PartETags = parts
                }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await s3.AbortMultipartUploadAsync(bucket, key, upload.UploadId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsStoreFailure(exception))
                {
                    // Amazon S3 keeps the parts until a lifecycle rule aborts the upload; some other stores clean them
                    // up on their own.
                }
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads into <paramref name="buffer"/> until it's full or a read returns nothing.</summary>
    private static async Task<int> FillAsync(Stream content, byte[] buffer, CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < PartSize)
        {
            int read = await content.ReadAsync(buffer.AsMemory(filled, Math.Min(PartSize - filled, ReadSize)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }
        return filled;
    }

    /// <summary>
    /// Lists the objects under <paramref name="prefix"/> after <paramref name="startAfter"/> that <paramref name="live"/>
    /// keeps, in key order, a batch at a time: as many as <paramref name="wanted"/> says are still needed, up to what's
    /// read at once.
    /// </summary>
    private async IAsyncEnumerable<string[]> ListKeysAsync(string prefix, string? startAfter, Func<S3Object, bool> live,
        Func<int> wanted, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new List<string>();
        await foreach (S3Object item in ListAsync(prefix, startAfter, cancellationToken).ConfigureAwait(false))
        {
            if (!live(item)) continue;
            batch.Add(item.Key);
            if (batch.Count >= Math.Clamp(wanted(), 1, ParallelReads * 4))
            {
                yield return [.. batch];
                batch.Clear();
            }
        }
        if (batch.Count > 0) yield return [.. batch];
    }

    private async IAsyncEnumerable<S3Object> ListAsync(string prefix, string? startAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, StartAfter = startAfter };
        ListObjectsV2Response response;
        do
        {
            response = await s3.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (S3Object item in response.S3Objects ?? []) yield return item;
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    /// <summary>Reads every key in <paramref name="keys"/>, several at once, keeping their order.</summary>
    private async Task<(string Key, T? Value)[]> ReadAllAsync<T>(string[] keys, CancellationToken cancellationToken) where T : class
    {
        var values = new (string, T?)[keys.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, keys.Length),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelReads, CancellationToken = cancellationToken },
            async (i, ct) => values[i] = (keys[i], (await GetAsync<T>(keys[i], ct).ConfigureAwait(false)).Value)).ConfigureAwait(false);
        return values;
    }

    /// <summary>Deletes everything under a container's ID, a listing at a time.</summary>
    private async Task PurgeAsync(string containerId, CancellationToken cancellationToken)
    {
        while (true)
        {
            ListObjectsV2Response listed = await s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket, Prefix = _keys.Data(containerId) }, cancellationToken).ConfigureAwait(false);
            if (listed.S3Objects is not { Count: > 0 } objects) return;
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket, Objects = [.. objects.Select(o => new KeyVersion { Key = o.Key })]
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes what a write left under a deleted container's ID. If that fails, what's left stays, but nothing reads it.
    /// </summary>
    private async Task PurgeQuietlyAsync(string containerId)
    {
        try
        {
            await PurgeAsync(containerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            // Nothing reads under a deleted container's ID.
        }
    }

    /// <summary>
    /// Deletes content no head names anymore. If that fails, the content stays until its container is deleted.
    /// </summary>
    private async Task DeleteQuietlyAsync(string[] keys)
    {
        if (keys.Length == 0) return;
        try
        {
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket, Objects = [.. keys.Select(k => new KeyVersion { Key = k })]
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            // Unreachable either way: nothing names it.
        }
    }

    /// <summary>
    /// Whether a cleanup failed because the store refused it or couldn't be reached, rather than for a reason the
    /// caller should hear about; a cleanup is sent with no token, so a cancellation is the client's timeout.
    /// </summary>
    private static bool IsStoreFailure(Exception exception) =>
        exception is AmazonServiceException or HttpRequestException or IOException or TimeoutException or OperationCanceledException;

    /// <summary>A head as stored: JSON, or for a name with no versions, just the write ID.</summary>
    private static byte[] Serialize(Head head) =>
        head.Versions.Count == 0 ? Encoding.UTF8.GetBytes(head.WriteId!) : JsonSerializer.SerializeToUtf8Bytes(head);

    private static byte[] Serialize(ContainerRecord record) => JsonSerializer.SerializeToUtf8Bytes(record);

    private static ResourceInfo ToInfo(string containerName, string name, StoredVersion version, bool isLatest) => new()
    {
        ContainerName = containerName,
        Name = name,
        VersionId = version.VersionId,
        LastModified = version.LastModified,
        IsLatest = isLatest,
        ContentType = version.ContentType,
        ContentLength = version.ContentLength,
        ContentHash = version.ContentHash,
        Metadata = new Dictionary<string, string>(version.Metadata),
        CopiedFrom = version.CopiedFrom,
        ExpiresAt = version.ExpiresAt
    };

    private static ContainerInfo ToInfo(string name, ContainerRecord record) => new()
    {
        Name = name, Metadata = new Dictionary<string, string>(record.Metadata)
    };

    private static ResourceContainerNotFoundException ContainerMissing(string resourceContainerName) =>
        new($"The container {resourceContainerName} doesn't exist.") { ContainerName = resourceContainerName };

    /// <summary>An object's value and ETag, as read.</summary>
    private readonly record struct Stored<T>(T? Value, string? ETag) where T : class;

    /// <summary>
    /// A container's record: its ID, which the keys of what it holds start with, its metadata, and whether it's deleted.
    /// </summary>
    private sealed record ContainerRecord(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("m")] Dictionary<string, string> Metadata,
        [property: JsonPropertyName("deleted")] bool Deleted = false,
        [property: JsonPropertyName("w")] string? WriteId = null);

    /// <summary>A name's versions, in the order they were stored; the last is current.</summary>
    private sealed record Head(
        [property: JsonPropertyName("v")] List<StoredVersion> Versions,
        [property: JsonPropertyName("w")] string? WriteId = null);

    /// <summary>One version's attributes, and the key of its content.</summary>
    private sealed record StoredVersion(
        [property: JsonPropertyName("id")] string VersionId,
        [property: JsonPropertyName("b")] string Blob,
        [property: JsonPropertyName("n")] long ContentLength,
        [property: JsonPropertyName("h")] string ContentHash,
        [property: JsonPropertyName("t")] string? ContentType,
        [property: JsonPropertyName("m")] Dictionary<string, string> Metadata,
        [property: JsonPropertyName("at")] DateTimeOffset LastModified,
        [property: JsonPropertyName("x")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("o")] ResourceOrigin? CopiedFrom);
}
