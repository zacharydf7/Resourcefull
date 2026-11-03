namespace Resourcefull.ObjectStorage;

/// <summary>
/// What <see cref="ObjectStoreRepository"/> needs from an object store: objects read with an ETag, writes conditional
/// on it, uploads in parts, ranged reads, listings in key order and deletes. Each provider implements it on its store's
/// client.
/// </summary>
internal interface IObjectStore
{
    /// <summary>
    /// The conditional writes the store must honor, named in the error raised when it keeps refusing them, such as
    /// <c>If-Match and If-None-Match on PutObject</c>.
    /// </summary>
    string ConditionalWrites { get; }

    /// <summary>An object's bytes and ETag; both <see langword="null"/> when there's no object.</summary>
    Task<(byte[]? Body, string? ETag)> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="body"/> only if the object still has <paramref name="etag"/>, or doesn't exist when it's
    /// <see langword="null"/>. It's sent with no cancellation token, so a caller that cancels never leaves it unknown
    /// whether the write landed.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the store refused it because the object isn't as expected: changed, or created by
    /// another writer, or written already by this write, whose answer was lost.
    /// </returns>
    Task<bool> TryPutAsync(string key, byte[] body, string? etag);

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="key"/>, reading it with <see cref="ObjectParts.FillAsync"/>
    /// until a read returns nothing: in one request when it's smaller than <see cref="ObjectParts.PartSize"/>, and
    /// otherwise in parts of that size, abandoning them if the upload fails.
    /// </summary>
    Task UploadAsync(string key, Stream content, CancellationToken cancellationToken);

    /// <summary>The most objects <see cref="JoinAsync"/> can join into one.</summary>
    int MaxJoinedParts { get; }

    /// <summary>
    /// Writes <paramref name="key"/> as the content of <paramref name="parts"/>, in order, joined in the store so the
    /// bytes don't pass through the application. Each part but the last is at least <see cref="ObjectParts.PartSize"/>
    /// long, and none is longer than <see cref="ObjectParts.MaxJoinedPartSize"/>.
    /// </summary>
    Task JoinAsync(string key, IReadOnlyList<ObjectRange> parts, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the bytes from <paramref name="start"/> up to <paramref name="end"/> of an object
    /// <paramref name="length"/> bytes long, asking the store for only that range when it isn't the whole object.
    /// </summary>
    /// <returns>The read, or <see langword="null"/> when there's no object.</returns>
    Task<ObjectRead?> OpenReadAsync(string key, long start, long end, long length, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the objects whose keys start with <paramref name="prefix"/> and come after <paramref name="startAfter"/>,
    /// in key order, which the keys' escaping makes the names' code point order.
    /// </summary>
    IAsyncEnumerable<ObjectListing> ListAsync(string prefix, string? startAfter, CancellationToken cancellationToken);

    /// <summary>Deletes the objects, ignoring any that don't exist.</summary>
    Task DeleteAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);

    /// <summary>
    /// Whether a cleanup failed because the store refused it or couldn't be reached, rather than for a reason the
    /// caller should hear about; a cleanup is sent with no token, so a cancellation is the client's timeout.
    /// </summary>
    bool IsStoreFailure(Exception exception);
}

/// <summary>An object's bytes as the store sends them, and what to dispose of when they've been read.</summary>
internal sealed record ObjectRead(Stream Body, IDisposable Owner);

/// <summary>A part of a join: <paramref name="Length"/> bytes of the object at <paramref name="Key"/>, from <paramref name="Offset"/>.</summary>
internal readonly record struct ObjectRange(string Key, long Offset, long Length);

/// <summary>An object in a listing: its key, its size in bytes, and when it was last written.</summary>
internal readonly record struct ObjectListing(string Key, long Size, DateTimeOffset LastModified);

/// <summary>How uploads read their content: in parts, a small slice at a time.</summary>
internal static class ObjectParts
{
    /// <summary>
    /// Content smaller than this goes up in one request; the rest goes in parts of this size, so memory stays bounded.
    /// </summary>
    internal const int PartSize = 8 * 1024 * 1024;

    /// <summary>
    /// The most one part of a join copies: within what both stores copy in one request (5 GiB for S3's
    /// <c>UploadPartCopy</c>, 4,000 MiB for Azure's <c>Put Block From URL</c>), and few enough parts for any version.
    /// </summary>
    internal const long MaxJoinedPartSize = 1024 * 1024 * 1024;

    /// <summary>
    /// The most one read of the content asks for. An upload inspector's refusal stops the upload at the next read, so
    /// reading a part in small slices keeps the upload from reading content it then throws away.
    /// </summary>
    private const int ReadSize = 81920;

    /// <summary>Reads into <paramref name="buffer"/>, a slice at a time, until it holds a part or a read returns nothing.</summary>
    /// <returns>How many bytes it read.</returns>
    public static async Task<int> FillAsync(Stream content, byte[] buffer, CancellationToken cancellationToken)
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
}
